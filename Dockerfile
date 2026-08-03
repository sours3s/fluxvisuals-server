# --- Стадия сборки: SDK ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY AuthServer/ ./AuthServer/
RUN dotnet publish AuthServer/AuthServer.csproj -c Release -o /app

# --- Стадия запуска: лёгкий runtime ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# Устанавливаем недостающую системную библиотеку для Npgsql/GSSAPI
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app ./
EXPOSE 5001

# Render задаёт PORT (обычно 10000) — подхватываем; иначе 5001.
CMD ["sh", "-c", "dotnet AuthServer.dll --urls http://0.0.0.0:${PORT:-5001}"]