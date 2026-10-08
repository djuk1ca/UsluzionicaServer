#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════════
# Brisanje SVIH korisničkih podataka — prelaz sa zatvorenog testiranja na
# javno izdanje. Posle ovoga server je kao pri prvom startu: prazna baza sa
# kategorijama, ulogama i admin nalogom iz .env-a.
#
# Na serveru: /opt/usluzionica/reset-data.sh (vlasnik root, 755), pokreće se
# RUČNO, nikad iz cron-a ni CD-a. Uputstvo: Obsidian „Uputstva za produkciju /
# 08 - Brisanje test podataka pred javno izdanje".
#
# BRIŠE: bazu (nalozi, oglasi, poruke, ocene, tokeni, rezervacije, prijave,
#        obaveštenja, uređaji za push), slike oglasa i profila, logove API-ja
#        (sadrže id-jeve i, u starijim zapisima, mejlove testera), keš u Redisu.
# NE DIRA: listu čekanja sa sajta (volumen sajta), TLS sertifikate (Caddy),
#        .env osim JWT_SECRET-a, backup-e, ključeve za zaštitu podataka u Redisu.
#
# JWT_SECRET se menja namerno: testeri na telefonima imaju sačuvanu sesiju
# korisnika koji posle ovoga ne postoji. Sa novim ključem ta sesija odmah
# postaje nevažeća i aplikacija ih vrati na prijavu, umesto da do isteka
# tokena rade sa nalogom koga nema.
# ═══════════════════════════════════════════════════════════════════════════
set -euo pipefail

DIR=/opt/usluzionica
DB_NAME=UsluzionicaDB
UPLOADS_VOLUME=usluzionica_uploads
LOGS_VOLUME=usluzionica_logs

cd "$DIR"
compose=(docker compose -f docker-compose.yml -f docker-compose.prod.yml)

log() { echo "[$(date '+%F %T')] $*"; }

sql() {
  "${compose[@]}" exec -T db sh -c \
    'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -b -Q "$1"' \
    _ "$1"
}

# ── 0. Potvrda ──────────────────────────────────────────────────────────────
cat <<'UPOZORENJE'

  Ovo NEPOVRATNO briše sve naloge, oglase, poruke, ocene, tokene i slike
  na produkciji. Pre brisanja pravi se backup trenutnog stanja.

UPOZORENJE
"${compose[@]}" ps --services --filter status=running | grep -qx db \
  || { echo "db kontejner ne radi — prekid."; exit 1; }
sql "SET NOCOUNT ON; SELECT (SELECT COUNT(*) FROM [$DB_NAME].dbo.AspNetUsers) AS korisnika, (SELECT COUNT(*) FROM [$DB_NAME].dbo.Listings) AS oglasa"
echo
read -r -p "Za potvrdu upiši tačno: OBRISI SVE  > " potvrda
[ "$potvrda" = "OBRISI SVE" ] || { echo "Odustano, ništa nije promenjeno."; exit 1; }

# ── 1. Poslednji backup starog stanja ───────────────────────────────────────
# Bez njega se ne nastavlja: ako se ispostavi da je nešto trebalo sačuvati,
# ovo je jedini put nazad. Na Storage Box-u ga snapshot-ovi čuvaju još 7 dana,
# posle čega nestaje sam — podaci testera se ne drže duže nego što treba.
[ -x "$DIR/backup.sh" ] || { echo "Nema $DIR/backup.sh — prvo uputstvo 02 (backup)."; exit 1; }
log "Backup pre brisanja"
"$DIR/backup.sh"

# ── 2. API stoji dok se briše ───────────────────────────────────────────────
log "Zaustavljam API"
"${compose[@]}" stop api

# ── 3. Baza ─────────────────────────────────────────────────────────────────
# SINGLE_USER ... ROLLBACK IMMEDIATE prekida eventualne preostale konekcije
# (npr. sqlcmd iz druge sesije) — bez toga DROP čeka ili pada.
log "Brišem bazu $DB_NAME"
sql "ALTER DATABASE [$DB_NAME] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$DB_NAME];"

# ── 4. Slike i logovi ───────────────────────────────────────────────────────
# Sadržaj volumena, ne sam volumen: compose ga ne mora ponovo praviti, a
# montiranja u kontejnerima ostaju ista.
log "Brišem slike ($UPLOADS_VOLUME) i logove ($LOGS_VOLUME)"
docker run --rm -v "$UPLOADS_VOLUME":/data alpine:3 find /data -mindepth 1 -delete
docker run --rm -v "$LOGS_VOLUME":/data alpine:3 find /data -mindepth 1 -delete

# ── 5. Keš ──────────────────────────────────────────────────────────────────
# Ne FLUSHALL: pod istim prefiksom su i ključevi za zaštitu podataka
# (usluzionica:dataprotection-keys). Briše se sve ostalo — keš oglasa,
# provajdera, „online" oznake, zaključavanja.
log "Brišem keš u Redisu (bez ključeva za zaštitu podataka)"
"${compose[@]}" exec -T cache sh -c \
  "redis-cli --scan --pattern 'usluzionica:*' | grep -v '^usluzionica:dataprotection-keys\$' | xargs -r -n 100 redis-cli DEL >/dev/null"

# ── 6. Nov JWT_SECRET ───────────────────────────────────────────────────────
if grep -q '^JWT_SECRET=' .env; then
  log "Nov JWT_SECRET (sve stare sesije postaju nevažeće)"
  novi=$(openssl rand -base64 48 | tr -d '\n/+=')
  sed -i "s|^JWT_SECRET=.*|JWT_SECRET=${novi}|" .env
else
  log "UPOZORENJE: JWT_SECRET nije nađen u .env — stare sesije ostaju važeće do isteka"
fi

# ── 7. Start: migracije prave praznu bazu, seed dodaje uloge i admina ───────
log "Pokrećem API (migracije + seed)"
"${compose[@]}" up -d api

for i in $(seq 1 36); do
  if "${compose[@]}" exec -T api curl -fsS -o /dev/null localhost:8080/health 2>/dev/null; then
    log "API zdrav posle ~$((i * 5)) s"
    break
  fi
  [ "$i" -eq 36 ] && { log "API se nije podigao za 3 min — pogledaj: ${compose[*]} logs --tail 80 api"; exit 1; }
  sleep 5
done

# ── 8. Provera ──────────────────────────────────────────────────────────────
log "Stanje posle brisanja (očekivano: 1 korisnik = admin, 188+ kategorija, 0 oglasa):"
sql "SET NOCOUNT ON; SELECT (SELECT COUNT(*) FROM [$DB_NAME].dbo.AspNetUsers) AS korisnika, (SELECT COUNT(*) FROM [$DB_NAME].dbo.Categories) AS kategorija, (SELECT COUNT(*) FROM [$DB_NAME].dbo.Listings) AS oglasa"

log "Gotovo. Sledeći korak: backup praznog stanja (/opt/usluzionica/backup.sh) i izdanje."
