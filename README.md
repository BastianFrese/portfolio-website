# portfolio-website

Persönliche Portfolioseite — live unter **https://bastian-frese.de**.

ASP.NET Core MVC (net10.0), betrieben als systemd-Dienst `portfolio` in LXC 402
("portfolio-ws") auf prox1. Auslieferung: nginx (Container) → Nginx Proxy Manager /
Cloudflare-Tunnel → Internet. Deployment per GitHub Actions (ERDI-Muster:
Releases-Verzeichnis + `current`-Symlink + Smoke-Test), ausgelöst bei jedem Push
auf `main`.

## Benötigte Repository-Secrets (Environment: `production`)

| Secret | Inhalt |
|---|---|
| `DEPLOY_HOST` | SSH-Host des Webservers (192.168.100.4) |
| `DEPLOY_PORT` | SSH-Port |
| `DEPLOY_USER` | SSH-Benutzer (`root`) |
| `DEPLOY_SSH_KEY` | privater Deploy-Schlüssel (ed25519) |
| `DEPLOY_APP_DIR` | `/var/www/portfolio` (enthält `releases/` + `current`) |

⚠️ Der GitHub-Runner muss den Server per SSH erreichen können — gleiches Muster wie
bei ERDI lösen (Port-Forward / Tunnel-SSH, analog DEPLOY_HOST bei Erdi-ERC).

## Struktur

```
Portfolio.csproj         ASP.NET Core MVC (kein EF, keine externen Pakete)
Program.cs               Minimal Hosting + /health/live
Controllers/             HomeController (Startseite)
Views/                   Razor: _Layout + Home/Index (kompletter Seiteninhalt)
wwwroot/css/site.css     Styles (kein Framework)
.github/workflows/       Deploy-Pipeline (dotnet publish linux-x64 → tar → scp
                         → releases/TS + current → systemctl restart portfolio
                         → Smoke-Test auf /health/live)
```

## Server-Seite (LXC 402)

- `/etc/systemd/system/portfolio.service` — RunningDirectory
  `/var/www/portfolio/current`, `dotnet Portfolio.dll`, Port 5000
- `/etc/nginx/sites-available/portfolio` — :80 → 127.0.0.1:5000 (default_server)
- Cloudflare-Tunnel-Ingress (`bastian-frese.de` → `http://192.168.100.4:80`) in
  `/etc/cloudflared/config.yml` auf LXC 109 (NPM)

⚠️ Enthält keine persönlichen Daten jenseits der Kontakt-E-Mail — öffentlich per Definition.