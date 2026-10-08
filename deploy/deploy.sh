#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════════
# Deploy jedne verzije API-ja — JEDINO što CD-ov SSH ključ sme da pokrene.
#
# Na serveru: /opt/usluzionica/deploy.sh, vlasnik root, 755 (deploy ne sme da
# je menja). Uputstvo: Obsidian „Uputstva za produkciju / 04 - SSH ključ za CD".
#
# DVA NAČINA POZIVA
#
#   1. Forced command (posle C3): u authorized_keys stoji
#        command="/opt/usluzionica/deploy.sh",no-pty,... ssh-ed25519 AAAA… github-cd
#      sshd tada IGNORIŠE komandu koju CD pošalje i pokreće ovu skriptu;
#      poslata komanda stiže u SSH_ORIGINAL_COMMAND.
#   2. Direktno (pre C3, ili ručno): deploy.sh DEPLOY_TAG=sha-abc1234
#
# Iz zahteva se čita SAMO tag, i to samo u obliku sha-<hex>. Sve ostalo se
# ignoriše. Ključ koji procuri zato može jedino da pusti neku od slika koje je
# CI već objavio — ne može da čita .env, menja compose ni pokrene shell.
# ═══════════════════════════════════════════════════════════════════════════
set -euo pipefail

zahtev="${SSH_ORIGINAL_COMMAND:-$*}"
if [[ ! "$zahtev" =~ DEPLOY_TAG=(sha-[0-9a-f]{7,40}) ]]; then
  echo "odbijeno: očekivan DEPLOY_TAG=sha-<hex>" >&2
  exit 1
fi
tag="${BASH_REMATCH[1]}"

cd /opt/usluzionica
compose=(docker compose -f docker-compose.yml -f docker-compose.prod.yml)

# Tag ide u .env; compose ga čita kao ${IMAGE_TAG}. Server tako uvek zna koja
# verzija se vrti, a rollback je ista skripta sa prethodnim tagom.
prethodni=$(grep -E '^IMAGE_TAG=' .env | cut -d= -f2- || true)
sed -i '/^IMAGE_TAG=/d' .env
echo "IMAGE_TAG=${tag}" >> .env
echo "Deploy: ${prethodni:-nepoznat} -> ${tag}"
echo "Rollback: /opt/usluzionica/deploy.sh DEPLOY_TAG=${prethodni:-sha-PRETHODNI}"

"${compose[@]}" pull api
"${compose[@]}" up -d api

# Stare slike (~390 MB svaka). -a jer naše verzije imaju tag pa nisu
# „dangling"; until=168h čuva poslednjih 7 dana za brz rollback. Slike koje
# koristi neki kontejner (mssql, redis, caddy) se ne brišu.
docker image prune -af --filter "until=168h"
