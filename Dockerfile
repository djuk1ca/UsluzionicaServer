# syntax=docker/dockerfile:1

# ═════════════════════════════════════════════════════════════════════════════
# FAZA 1 — BUILD
# SDK slika (~800 MB) ima kompajler i alate. Koristi se SAMO za građenje;
# u finalnu sliku ne prelazi ništa osim rezultata publish-a.
# ═════════════════════════════════════════════════════════════════════════════
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# ── Layer caching ────────────────────────────────────────────────────────────
# Prvo SAMO .csproj, pa restore, pa tek onda ostatak koda.
# Sloj `restore` se poništava tek kad se promeni .csproj (dodat/uklonjen paket),
# a ne pri svakoj izmeni .cs fajla. Ušteda 30–60 s po build-u.
#
# Restore iz .csproj a NE iz .sln: solution referencira i oba test projekta,
# pa bi povukao Testcontainers, xunit i Respawn — nepotrebno u slici.
COPY UsluzionicaServer.csproj .
RUN dotnet restore UsluzionicaServer.csproj

COPY . .

# UseAppHost=false → ne pravi se native .exe, pokreće se kroz `dotnet X.dll`.
# U kontejneru nam native launcher ne treba i samo dodaje veličinu.
RUN dotnet publish UsluzionicaServer.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# ═════════════════════════════════════════════════════════════════════════════
# FAZA 2 — RUNTIME
# ASP.NET slika (~220 MB) nema kompajler, SDK ni izvorni kod.
#
# NE koristi -alpine ni -chiseled varijante: one nemaju ICU, a SearchNormalizer
# zove String.Normalize(FormD) koji bez ICU baca PlatformNotSupportedException.
# Cela pretraga na srpskom bi pukla. Debian slika ima pun ICU.
# ═════════════════════════════════════════════════════════════════════════════
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Non-root korisnik ne sme da otvori port ispod 1024 — otud 8080, ne 80.
ENV ASPNETCORE_HTTP_PORTS=8080

# ── curl, isključivo za healthcheck ──────────────────────────────────────────
# Debian .NET runtime slika je svedena i NEMA curl, wget, nc ni python.
# Docker healthcheck koji zove nepostojeću komandu ne prijavljuje grešku koju
# ćeš primetiti — kontejner prosto zauvek stoji `unhealthy`, a svaki
# `depends_on: service_healthy` na njemu se nikad ne otključa.
#
# Ide PRE `COPY --from=build` da izmena koda ne obara ovaj sloj, i PRE
# `USER $APP_UID` jer apt traži root.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# wwwroot MORA postojati pre starta aplikacije.
# IWebHostEnvironment.WebRootPath je null ako folder ne postoji, pa bi svaki
# Path.Combine(env.WebRootPath, ...) bacio ArgumentNullException pri prvom
# upload-u slike. Folder je prazan u slici — sadržaj dolazi sa volumena.
#
# chown je neophodan jer aplikacija radi kao non-root i mora imati pravo pisanja.
RUN mkdir -p /app/wwwroot/uploads/avatars \
             /app/wwwroot/uploads/covers \
             /app/wwwroot/uploads/listings \
 && chown -R $APP_UID:$APP_UID /app/wwwroot

# ── Non-root ─────────────────────────────────────────────────────────────────
# .NET 8 slike već sadrže korisnika `app` (UID 1654) izloženog kroz $APP_UID.
# Bez ovoga kontejner radi kao root: probijanje aplikacije = root u kontejneru.
USER $APP_UID

EXPOSE 8080
ENTRYPOINT ["dotnet", "UsluzionicaServer.dll"]