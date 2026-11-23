FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/Matchmaking.Stats/Matchmaking.Stats.csproj src/Matchmaking.Stats/
COPY src/Matchmaking.Contracts/Matchmaking.Contracts.csproj src/Matchmaking.Contracts/
RUN dotnet restore src/Matchmaking.Stats/Matchmaking.Stats.csproj
COPY src/ src/
RUN dotnet publish src/Matchmaking.Stats/Matchmaking.Stats.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Matchmaking.Stats.dll"]
