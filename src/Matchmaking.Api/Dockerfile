FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/Matchmaking.Api/Matchmaking.Api.csproj src/Matchmaking.Api/
RUN dotnet restore src/Matchmaking.Api/Matchmaking.Api.csproj
COPY src/ src/
RUN dotnet publish src/Matchmaking.Api/Matchmaking.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Matchmaking.Api.dll"]
