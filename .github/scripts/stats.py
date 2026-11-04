"""Salva ogni giorno le statistiche di AppIP nel ramo `stats`.

- downloads.csv: contatore cumulativo dei download di ogni file allegato alle release
- traffic.csv:   visite e clone giornalieri (solo se è configurato il secret TRAFFIC_TOKEN)
- README.md:     riepilogo leggibile
"""
import csv
import datetime
import json
import os
import urllib.request

REPO = os.environ["GITHUB_REPOSITORY"]


def api(path, token):
    req = urllib.request.Request(
        "https://api.github.com/" + path,
        headers={
            "Authorization": "Bearer " + token,
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "appip-stats",
        },
    )
    with urllib.request.urlopen(req, timeout=30) as r:
        return json.load(r)


def all_releases(token):
    out, page = [], 1
    while True:
        data = api("repos/%s/releases?per_page=100&page=%d" % (REPO, page), token)
        if not data:
            return out
        out += data
        page += 1


def load(path, key_len):
    if not os.path.exists(path):
        return {}
    with open(path, newline="", encoding="utf-8") as f:
        rows = list(csv.reader(f))[1:]
    return {tuple(r[:key_len]): r for r in rows if r}


def save(path, header, rows):
    with open(path, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(header)
        w.writerows(sorted(rows))


today = datetime.datetime.now(datetime.timezone.utc).date().isoformat()

# ---------- download delle release ----------
downloads = load("downloads.csv", 3)
for rel in all_releases(os.environ["GITHUB_TOKEN"]):
    for a in rel.get("assets", []):
        downloads[(today, rel["tag_name"], a["name"])] = [today, rel["tag_name"], a["name"], str(a["download_count"])]
save("downloads.csv", ["data", "versione", "file", "download_totali"], downloads.values())

# ---------- traffico (facoltativo) ----------
traffic = load("traffic.csv", 1)
traffic_token = os.environ.get("TRAFFIC_TOKEN", "").strip()
traffic_note = "Visite e clone non registrati: manca il secret `TRAFFIC_TOKEN`."
if traffic_token:
    try:
        views = api("repos/%s/traffic/views" % REPO, traffic_token)["views"]
        clones = api("repos/%s/traffic/clones" % REPO, traffic_token)["clones"]
        days = {}
        for x in views:
            days.setdefault(x["timestamp"][:10], [0, 0, 0, 0])[0:2] = [x["count"], x["uniques"]]
        for x in clones:
            days.setdefault(x["timestamp"][:10], [0, 0, 0, 0])[2:4] = [x["count"], x["uniques"]]
        # GitHub fornisce gli ultimi 14 giorni (anche quelli prima della creazione del repo, a zero):
        # i valori più recenti sostituiscono quelli parziali già salvati
        created = api("repos/%s" % REPO, os.environ["GITHUB_TOKEN"])["created_at"][:10]
        for d, v in days.items():
            if d >= created:
                traffic[(d,)] = [d] + [str(n) for n in v]
        traffic = {k: r for k, r in traffic.items() if k[0] >= created}
        save("traffic.csv", ["data", "visite", "visitatori_unici", "clone", "clone_unici"], traffic.values())
        traffic_note = ""
    except Exception as e:  # token scaduto o senza permessi: i download vengono salvati comunque
        traffic_note = "Visite e clone non aggiornati oggi: %s" % e
        print(traffic_note)
        # Diagnostica senza rivelare il token: tipo (dal prefisso), lunghezza, messaggio di GitHub
        kind = ("fine-grained" if traffic_token.startswith("github_pat_") else
                "classic" if traffic_token.startswith("ghp_") else "formato sconosciuto")
        print("TRAFFIC_TOKEN: %s, %d caratteri" % (kind, len(traffic_token)))
        if hasattr(e, "read"):
            try:
                print("Risposta di GitHub: %s" % json.loads(e.read().decode()).get("message"))
            except Exception:
                pass

# ---------- riepilogo ----------
totals = {}
latest = {}
for (d, tag, name), r in downloads.items():
    totals[d] = totals.get(d, 0) + int(r[3])
    if d == today:
        latest[(tag, name)] = int(r[3])

lines = ["# Statistiche AppIP", "", "Aggiornato il %s (UTC) dalla GitHub Action *Statistiche download*." % today, ""]
lines += ["## Download totali per file", "", "| Versione | File | Download |", "|---|---|---:|"]
for (tag, name), n in sorted(latest.items(), reverse=True):
    lines.append("| %s | %s | %d |" % (tag, name, n))
lines += ["| | **Totale** | **%d** |" % sum(latest.values()), ""]

lines += ["## Nuovi download per giorno (ultimi 30 giorni)", "", "| Giorno | Nuovi | Totale |", "|---|---:|---:|"]
dates = sorted(totals)
for i in range(len(dates) - 1, max(-1, len(dates) - 31), -1):
    d = dates[i]
    new = totals[d] - totals[dates[i - 1]] if i > 0 else totals[d]
    lines.append("| %s | %d | %d |" % (d, new, totals[d]))
lines.append("")

lines += ["## Visite e clone", ""]
if traffic:
    lines += ["| Giorno | Visite | Visitatori unici | Clone | Clone unici |", "|---|---:|---:|---:|---:|"]
    for (d,), r in sorted(traffic.items(), reverse=True)[:30]:
        lines.append("| %s | %s | %s | %s | %s |" % tuple(r))
    lines.append("")
if traffic_note:
    lines += [traffic_note, ""]
lines += ["Dati completi: [downloads.csv](downloads.csv)" + (" · [traffic.csv](traffic.csv)" if traffic else ""), ""]

with open("README.md", "w", encoding="utf-8") as f:
    f.write("\n".join(lines))
print("Totale download oggi: %d" % sum(latest.values()))
