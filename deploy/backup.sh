#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════════
# Noćni backup: baza + slike oglasa → lokalno → Hetzner Storage Box.
#
# Na serveru: /opt/usluzionica/backup.sh (vlasnik root, 755), pokreće ga cron
# kao korisnik deploy (deploy/usluzionica-backup.cron). Uputstvo: Obsidian
# „Uputstva za produkciju / 02 - Backup baze i slika".
#
# ŠTA RADI
#   1. BACKUP DATABASE u db kontejneru → kopija napolje → provera → gzip
#   2. tar volumena sa slikama (samo čitanje)
#   3. briše lokalne kopije starije od KEEP_DAYS
#   4. rsync na Storage Box (ogledalo lokalnog foldera); istoriju (7 dnevnih,
#      4 nedeljna) drže SNAPSHOT-ovi Storage Box-a — njih ključ sa ovog
#      servera ne može da obriše, pa ni kompromitovan server ne briše istoriju
#   5. opciono: javi healthchecks.io da je prošlo (izostanak javljanja = mejl)
#
# ZAŠTO BEZ `WITH COMPRESSION`
#   Produkcija je SQL Server Express (docker-compose.prod.yml: MSSQL_PID), a
#   Express ne podržava kompresiju backup-a — komanda bi pala. Zato običan
#   .bak, pa gzip (baza ovog tipa se sabija ~5–10×).
#
# Podešavanja: /opt/usluzionica/backup.env (nije u gitu), npr.
#   STORAGEBOX=u123456@u123456.your-storagebox.de
#   HEALTHCHECK_URL=https://hc-ping.com/<uuid>
# ═══════════════════════════════════════════════════════════════════════════
set -euo pipefail

DIR=/opt/usluzionica
BACKUP_DIR="$DIR/backups"
KEEP_DAYS=3
DB_NAME=UsluzionicaDB
UPLOADS_VOLUME=usluzionica_uploads
STORAGEBOX=""
STORAGEBOX_KEY="$HOME/.ssh/storagebox"
HEALTHCHECK_URL=""

# shellcheck source=/dev/null
[ -f "$DIR/backup.env" ] && . "$DIR/backup.env"

cd "$DIR"
compose=(docker compose -f docker-compose.yml -f docker-compose.prod.yml)
datum=$(date +%Y-%m-%d_%H%M)
mkdir -p "$BACKUP_DIR"
chmod 700 "$BACKUP_DIR"   # u backup-u su svi podaci korisnika

log() { echo "[$(date '+%F %T')] $*"; }

# Ako bilo šta padne, healthchecks.io dobija /fail — mejl stiže odmah, a ne
# tek kad izostane sledeće javljanje.
neuspeh() {
  log "BACKUP NIJE USPEO (red $1)"
  [ -n "$HEALTHCHECK_URL" ] && curl -fsS -m 10 --retry 3 "$HEALTHCHECK_URL/fail" >/dev/null || true
}
trap 'neuspeh $LINENO' ERR

# ── 1. Baza ─────────────────────────────────────────────────────────────────
# Lozinka se ne prenosi kroz komandnu liniju: kontejner već ima
# MSSQL_SA_PASSWORD u okruženju (compose ga postavlja), sqlcmd je čita iz
# SQLCMDPASSWORD unutar kontejnera.
sql() {
  "${compose[@]}" exec -T db sh -c \
    'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -b -Q "$1"' \
    _ "$1"
}

unutra=/var/opt/mssql/backup/usluzionica.bak
"${compose[@]}" exec -T db mkdir -p /var/opt/mssql/backup

log "Baza: BACKUP DATABASE [$DB_NAME]"
sql "BACKUP DATABASE [$DB_NAME] TO DISK = N'$unutra' WITH INIT, FORMAT, CHECKSUM"

# Backup koji se ne može vratiti nije backup — provera pre nego što se išta briše.
sql "RESTORE VERIFYONLY FROM DISK = N'$unutra' WITH CHECKSUM"

docker cp "$("${compose[@]}" ps -q db):$unutra" "$BACKUP_DIR/baza_$datum.bak"
"${compose[@]}" exec -T db rm -f "$unutra"
gzip -f "$BACKUP_DIR/baza_$datum.bak"
log "Baza: $(du -h "$BACKUP_DIR/baza_$datum.bak.gz" | cut -f1)"

# ── 2. Slike ────────────────────────────────────────────────────────────────
# Volumen se montira samo za čitanje u privremeni kontejner; API radi dalje.
log "Slike: volumen $UPLOADS_VOLUME"
docker run --rm \
  -v "$UPLOADS_VOLUME":/data:ro \
  -v "$BACKUP_DIR":/out \
  alpine:3 tar czf "/out/slike_$datum.tar.gz" -C /data .
log "Slike: $(du -h "$BACKUP_DIR/slike_$datum.tar.gz" | cut -f1)"

# ── 3. Lokalno čuvanje ──────────────────────────────────────────────────────
find "$BACKUP_DIR" -maxdepth 1 -type f \( -name 'baza_*' -o -name 'slike_*' \) -mtime +"$KEEP_DAYS" -delete

# ── 4. Van servera ──────────────────────────────────────────────────────────
if [ -n "$STORAGEBOX" ]; then
  log "Storage Box: rsync"
  rsync -a --delete \
    -e "ssh -p 23 -i $STORAGEBOX_KEY -o BatchMode=yes -o StrictHostKeyChecking=yes" \
    "$BACKUP_DIR/" "$STORAGEBOX:usluzionica/"
else
  # Namerno ne pada: lokalni backup je bolji od nijednog. Ali se vidi u logu.
  log "UPOZORENJE: STORAGEBOX nije podešen — backup postoji SAMO na ovom serveru"
fi

# ── 5. Javljanje ────────────────────────────────────────────────────────────
[ -n "$HEALTHCHECK_URL" ] && curl -fsS -m 10 --retry 3 "$HEALTHCHECK_URL" >/dev/null
log "Gotovo."
