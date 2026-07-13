// k6 yük testi: kayıt -> kuyruğa giriş -> eşleşme bulunana kadar bekleme.
// Ölçülen ana metrik: match_time (kuyruğa girişten lobi oluşana kadar geçen süre).
//
// Çalıştırma (sistem docker compose ile ayakta olmalı):
//   k6 run loadtest/matchmaking.js
//   k6 run -e PLAYERS=1000 loadtest/matchmaking.js
//
// Not: Test sahte oyuncular (lt_*) yaratır. Temiz bir başlangıç için sonrasında:
//   docker compose down -v && docker compose up -d

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Trend, Counter } from 'k6/metrics';

const BASE = __ENV.BASE_URL || 'http://localhost:8080';
const PLAYERS = Number(__ENV.PLAYERS || 1000);

const matchTime = new Trend('match_time', true); // ms cinsinden, süre olarak raporlanır
const matchedPlayers = new Counter('matched_players');
const unmatchedPlayers = new Counter('unmatched_players');

export const options = {
  scenarios: {
    players: {
      executor: 'per-vu-iterations',
      vus: PLAYERS,
      iterations: 1,
      maxDuration: '5m',
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],   // isteklerin %99'u başarılı olmalı
    // bcrypt kasıtlı olarak pahalı: kayıt için ayrı, daha gevşek eşik
    'http_req_duration{name:auth/register}': ['p(95)<2000'],
    'http_req_duration{name:queue/join}': ['p(95)<500'],
    'http_req_duration{name:lobbies/mine}': ['p(95)<500'],
    match_time: ['p(95)<15000'],      // p95 eşleşme süresi 15 sn altı
  },
};

const JSON_HEADERS = { 'Content-Type': 'application/json' };

export default function () {
  // Kayıt yükünü zamana yay: bcrypt pahalıdır, 1000 eşzamanlı kayıt gerçekçi değil.
  // Oyuncular ~60 sn boyunca kademeli gelir (gerçek dünyada da login dalgası yayılıdır).
  sleep(Math.random() * Number(__ENV.RAMP_SECONDS || 60));

  // 1) Kayıt
  const username = `lt_${__VU}_${Date.now() % 100000000}`;
  let res = http.post(`${BASE}/auth/register`, JSON.stringify({
    username,
    email: `${username}@lt.local`,
    password: 'password123',
  }), { headers: JSON_HEADERS, tags: { name: 'auth/register' } });

  if (!check(res, { 'registered (200)': (r) => r.status === 200 })) return;
  const auth = res.json();
  const authHeaders = {
    Authorization: `Bearer ${auth.accessToken}`,
    'Content-Type': 'application/json',
  };

  // 2) Kuyruğa gir
  res = http.post(`${BASE}/queue/join`, null,
    { headers: authHeaders, tags: { name: 'queue/join' } });
  if (!check(res, { 'queued (200)': (r) => r.status === 200 })) return;

  const queuedAt = Date.now();

  // 3) Eşleşene kadar bekle (0.5 sn'de bir sorgula, en fazla 60 sn)
  for (let i = 0; i < 120; i++) {
    sleep(0.5);
    res = http.get(`${BASE}/lobbies/mine`,
      { headers: authHeaders, tags: { name: 'lobbies/mine' } });

    if (res.status === 200) {
      matchTime.add(Date.now() - queuedAt);
      matchedPlayers.add(1);
      return;
    }
  }

  unmatchedPlayers.add(1); // 60 sn içinde eşleşemedi (tek sayıda oyuncu kaldıysa normal)
}
