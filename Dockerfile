# Contexto de build precisa ser o diretório PAI de core/, core.domain/ e core.infrastructure/
# (três repos irmãos, referenciados via ProjectReference relativo em core.csproj) —
# ex.: docker build -f core/Dockerfile -t core:latest .  rodado a partir de re-colocar-me/,
# com os três repos já com checkout feito lado a lado (ver .github/workflows/deploy.yml).
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY core.domain/ ./core.domain/
COPY core.infrastructure/ ./core.infrastructure/
COPY core/ ./core/
# .dockerignore não se aplica aqui (o contexto de build é o diretório pai, não core/ em si) —
# removendo explicitamente pra não assar segredo de dev na imagem de produção.
RUN rm -f core/appsettings.Development.json
WORKDIR /src/core
RUN dotnet restore core.csproj
RUN dotnet publish core.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "core.dll"]
