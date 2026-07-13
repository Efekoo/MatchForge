# Gerçek Zamanlı Matchmaking & Lobby Servisi — Nihai Proje Planı

Rekabetçi online oyunların arkasındaki üç temel backend problemini (kimlik doğrulama, beceri bazlı eşleştirme, gerçek zamanlı oturum yönetimi) bağımsız, ölçeklenebilir bir servis olarak çözen portfolyo projesi. Oyun motoru gerektirmez; tamamen backend odaklıdır ancak problem alanı oyun dünyasından gelir. Bu sayede hem backend developer hem game developer başvurularında doğrudan konuşma malzemesidir.

**Teknoloji yığını:** C# / ASP.NET Core, PostgreSQL, Redis, SignalR, JWT, Docker Compose, nginx, GitHub Actions, k6, OpenTelemetry + Prometheus + Grafana.

---

## 1. Sistemin Uçtan Uca Akışı

Oyuncunun yaşadığı döngü şudur ve sistem bu döngüyü eksiksiz kapatır:

**Kayıt/Giriş → Kuyruğa girme → Eşleşme bulunması → Lobiye alınma → Mini maçın oynanması → Sonucun kaydedilmesi → Elo/MMR güncellemesi → Tekrar kuyruk**

Döngünün kapanması projenin en kritik tasarım kararıdır. Sadece "eşleşme bulundu" ekranında biten bir demo, hikâyeyi yarıda keser; maçın gerçekten oynanıp sonucun rating'e yansıması sistemi uçtan uca çalışır kılar.

---

## 2. Mimari

```
İstemci (basit web sayfası, SignalR client)
        │
      nginx (load balancer)
        │
ASP.NET Core API  ×2 replika
        │                    │
   PostgreSQL              Redis
   (kalıcı veri)      (kuyruk, lobi state,
                       SignalR backplane,
                       dağıtık kilitler)
        │
Matchmaker BackgroundService (kuyruk tarayıcı)
        │
Prometheus + Grafana (gözlem katmanı)
```

Tamamı tek `docker compose up` komutuyla ayağa kalkar.

### Sorumluluk dağılımı

- **PostgreSQL:** Kalıcı veri — oyuncular, maç geçmişi, MMR değişim kayıtları. Kaybolmaması gereken her şey.
- **Redis:** Geçici ve hızlı veri — matchmaking kuyruğu, aktif lobi state'i (TTL ile kendini temizler), SignalR backplane, oyuncu kilitleri. Postgres'e yazılması hem yavaş hem gereksiz olan her şey.
- **SignalR:** Gerçek zamanlı olay akışı — eşleşme bildirimi, lobi olayları, maç hamleleri.
- **BackgroundService:** Eşleştirme mantığını API isteklerinden bağımsız, periyodik çalışan bir süreç olarak izole eder.

---

## 3. Bileşenler

### 3.1 Kimlik Katmanı
- JWT tabanlı kayıt/giriş; **access token + refresh token** akışı (sadece access token yapan projelerden ayrışma noktası).
- Şifreler bcrypt veya argon2 ile hashlenir.
- SignalR bağlantıları JWT ile doğrulanır; connection id geçici, oyuncu kimliği kalıcıdır (reconnect senaryosunun temeli).

> Bu katman "hijyen"dir, fark yaratan yer değildir. Fazla zaman gömme.

### 3.2 Oyuncu Profili ve Derecelendirme
- Her oyuncunun bir MMR değeri vardır (başlangıç: 1000).
- Maç bitiminde **Elo formülü** ile güncelleme — kütüphane değil, kendi implementasyonun:
  - Beklenen skor: `E = 1 / (1 + 10^((Rb - Ra) / 400))`
  - Yeni rating: `Ra' = Ra + K × (S - E)` (S: kazanırsa 1, kaybederse 0)
  - **K faktörü:** yeni oyuncularda yüksek (ör. 40, ilk 20 maç), oturmuş oyuncularda düşük (ör. 20). Nedenini anlatabilmek mülakat altınıdır: yeni oyuncunun gerçek seviyesi bilinmez, hızlı yakınsama gerekir; oturmuş oyuncunun rating'i stabil kalmalıdır.
- Her MMR değişimi ayrı kayıt olarak tutulur (denetlenebilirlik + profil grafiği).

### 3.3 Matchmaking Kuyruğu — Projenin Kalbi
- Oyuncu `POST /queue/join` ile kuyruğa girer; kuyruk Redis'te tutulur (sorted set, skor = MMR).
- **Matchmaker BackgroundService** her 2 saniyede bir kuyruğu tarar.
- **Genişleyen aralık mekaniği:** kabul edilebilir MMR farkı bekleme süresiyle büyür.
  - 0–10 sn: ±50 MMR
  - her ek 10 sn: +50 (üst sınır: ±400)
- Bu, tüm rekabetçi oyunların (Clash Royale dahil) kullandığı temel trade-off'tur: **eşleşme kalitesi vs bekleme süresi**. Mülakatta anlatılacak hikâye budur.
- Eşleşme bulununca iki oyuncu atomik olarak kuyruktan çıkarılır, lobi oluşturulur, SignalR ile bildirilir.

### 3.4 Lobi ve Mini Maç
- Lobi state'i Redis'te, TTL ile (terk edilen lobiler kendini temizler).
- SignalR olayları: `MatchFound`, `OpponentReady`, `MatchStarted`, `MoveMade`, `MatchEnded`, `OpponentDisconnected`, `OpponentReconnected`.
- **Mini oyun: taş-kağıt-makas (best of 3) veya XOX.** Kasıtlı olarak basittir — bu bir özellik eksikliği değil tasarım kararıdır: "oyun mantığı değil, altyapı gösteriyorum." Hamle doğrulama sunucu tarafındadır (istemciye güvenilmez).
- Maç sonucu Postgres'e yazılır, Elo güncellenir, döngü kapanır.

### 3.5 Dayanıklılık: Concurrency ve Reconnect

Junior projelerinin %95'inin hiç sormadığı sorular — bu projenin en ayırt edici bölümü. README'de ayrı bir **"Concurrency & Resilience"** başlığı altında belgelenir.

**Yarış koşulları (race conditions):**
- Aynı oyuncu iki farklı maça atanabilir mi? → Eşleştirme sırasında Redis `SETNX` ile oyuncu bazlı dağıtık kilit; kilidi alamayan eşleştirme denemesi o oyuncuyu atlar.
- Aynı anda iki cihazdan kuyruğa girilirse? → Kuyruğa ekleme idempotent'tir; oyuncu zaten kuyruktaysa ikinci istek reddedilir.
- İki API replikası aynı anda eşleştirme yaparsa? → Matchmaker tek instance çalışır **veya** eşleştirme turu Redis kilidi ile serileştirilir (tercih edilen: kilit — yatay ölçeklenebilirlik hikâyesi bozulmaz).

**Bağlantı kopması (disconnect/reconnect):**
- Kuyruktayken kopan oyuncu: heartbeat/SignalR disconnect olayı üzerine 15 sn grace period, sonra kuyruktan düşürülür.
- Maç sırasında kopan oyuncu: lobi 30 sn açık tutulur; oyuncu yeniden bağlanınca (yeni connection id, aynı JWT kimliği) maça geri alınır ve güncel state gönderilir. Süre dolarsa hükmen mağlubiyet.

### 3.6 Ölçekleme
- Docker Compose'da API'den **2 replika**, önünde nginx.
- **SignalR Redis backplane:** aynı lobideki iki oyuncu farklı instance'lara bağlıysa mesajlar yine akar.
- **k6 ile yük testi**, sonuçlar README'de somut sayılarla.

### 3.7 Gözlemlenebilirlik
- **OpenTelemetry** (trace + metric + log).
- Prometheus + Grafana Compose'a dahil; dashboard'da canlı metrikler: kuyruk uzunluğu, ortalama / p95 bekleme süresi, aktif lobi sayısı, eşleşme MMR farkı dağılımı.

---

## 4. Veri Modeli (Özet)

**PostgreSQL:**

| Tablo | Alanlar (özet) |
|---|---|
| `players` | id, username, email, password_hash, mmr, games_played, created_at |
| `matches` | id, player_a, player_b, winner, mmr_delta_a, mmr_delta_b, started_at, ended_at |
| `mmr_history` | id, player_id, match_id, old_mmr, new_mmr, created_at |
| `refresh_tokens` | id, player_id, token_hash, expires_at, revoked |

**Redis key şeması:**

| Key | Tip | Amaç |
|---|---|---|
| `mm:queue` | sorted set (skor=MMR) | Matchmaking kuyruğu |
| `mm:queue:joined:{playerId}` | string (timestamp) | Kuyruğa giriş zamanı (aralık genişletme için) |
| `mm:lock:{playerId}` | string, SETNX + TTL | Oyuncu eşleştirme kilidi |
| `lobby:{lobbyId}` | hash, TTL | Lobi/maç state'i |
| `player:lobby:{playerId}` | string | Oyuncunun aktif lobisi (reconnect için) |

---

## 5. API Yüzeyi (Özet)

```
POST   /auth/register          POST   /auth/login
POST   /auth/refresh           POST   /auth/logout
GET    /players/me             GET    /players/me/history
POST   /queue/join             DELETE /queue/leave
GET    /queue/status
GET    /lobbies/{id}           (durum sorgulama; olaylar SignalR'da)
GET    /health                 GET    /metrics
```

SignalR hub: `/hubs/game` — bölüm 3.4'teki olaylar.

---

## 6. Teslimat Standardı
- **Tek komut:** `docker compose up` ile tüm sistem ayağa kalkar.
- **CI/CD:** GitHub Actions — build, unit + integration test (Testcontainers), Docker image build.
- **Testler:** Elo + eşleştirme unit test; kuyruk→eşleşme→maç→sonuç integration test; race condition paralel istek testleri.
- **README:** mimari diyagram, Swagger/OpenAPI, "Concurrency & Resilience" bölümü, yük testi sonuçları, demo GIF/video.
- **İstemci:** minimal web sayfası (kuyruk butonu, lobi ekranı, mini oyun).

---

## 7. Yol Haritası ve Kapsam Disiplini

En büyük risk teknik değil, **kapsam**tır. Sıralama katıdır; bir faz bitmeden sonrakine geçilmez.

### MVP (Hafta 1–2)
- JWT auth (access + refresh)
- Kuyruk + genişleyen aralıklı eşleştirme (BackgroundService)
- SignalR lobi + mini maç (taş-kağıt-makas)
- Elo güncellemesi, Postgres kayıtları
- Tek instance, Docker Compose, temel README

### v1.1 (Hafta 3)
- Redis dağıtık kilitler, idempotent kuyruk (race condition çözümleri)
- Disconnect/reconnect akışı
- 2 replika + nginx + SignalR Redis backplane
- k6 yük testi + sonuçların README'ye işlenmesi
- GitHub Actions pipeline, Testcontainers integration testleri

### v2 (sonrası, opsiyonel)
- Parti desteği (2v2 kuyruğu)
- OpenTelemetry + Grafana dashboard
- Sezonluk leaderboard, MMR decay

---

## 8. CV ve Mülakat Çıktıları

**Örnek CV bullet'ları (v1.1 sonrası gerçek sayılarla doldurulacak):**
- Built a horizontally scalable real-time matchmaking service (ASP.NET Core, SignalR, Redis, PostgreSQL) handling 1,000+ concurrent players with p95 match time under X seconds across 2 load-balanced instances.
- Implemented skill-based matchmaking with expanding Elo/MMR windows and resolved concurrency issues (duplicate matching, reconnection) using Redis distributed locks.
- Designed full CI/CD pipeline with GitHub Actions and Testcontainers-based integration tests; single-command deployment via Docker Compose.

**Mülakat hikâye başlıkları:**
1. Eşleşme kalitesi vs bekleme süresi trade-off'u ve genişleyen aralık tasarımı
2. K faktörü seçimi ve yeni oyuncu yakınsaması
3. Aynı oyuncunun çift eşleşmesini Redis kilidiyle nasıl engelledin
4. SignalR backplane neden gerekliydi
5. Reconnect'te connection id ile oyuncu kimliğini nasıl ayrıştırdın
