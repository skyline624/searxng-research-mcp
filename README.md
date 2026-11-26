# SearXNG Research MCP

Serveur MCP local en .NET 10, autonome. Il expose deux outils via **stdio** et utilise l’API JSON de SearXNG.

| Outil | Résultat |
| --- | --- |
| `web_search` | Liens, extraits et moteurs indisponibles. Filtres par langue, catégorie, moteur, période et page. Fonctionne sans LLM. |
| `research` | Planifie les requêtes, lit les pages HTML et PDF, affine la recherche, classe les passages et rédige une réponse avec des citations `[n]`. Renvoie la réponse, ses sources et les avertissements sur les citations. |

## Installation sur Windows

Prérequis pour ce mode : PowerShell 7, SDK .NET 10, Docker Compose pour SearXNG et, pour `research`, un serveur LLM compatible avec `POST /v1/chat/completions`.

```powershell
git clone https://github.com/skyline624/searxng-research-mcp.git
Set-Location searxng-research-mcp

Copy-Item .env.example .env
$searxSecret = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
(Get-Content .env) -replace '^SEARXNG_SECRET=.*$', "SEARXNG_SECRET=$searxSecret" | Set-Content .env
docker compose up -d searxng

Copy-Item appsettings.example.json appsettings.local.json
dotnet publish -c Release -o out
```

Si tu as déjà une instance SearXNG, renseigne son URL dans `Search.SearxngUrl` et autorise le format `json` dans ses réglages. Le service Docker inclus écoute sur `127.0.0.1:18888`. [API SearXNG](https://docs.searxng.org/dev/search_api.html)

### Configurer le LLM

Édite `appsettings.local.json` avant d’appeler `research`.

- `OpenAI.BaseUrl` correspond à la base de l’API, par exemple `http://127.0.0.1:11434/v1` ou `http://127.0.0.1:1234/v1`, selon le serveur.
- `OpenAI.Model` est l’identifiant du modèle installé. `AnalysisModel` peut le remplacer pour la planification, la sélection des candidats et les tours de recherche. `AnswerModel` peut le remplacer pour la rédaction.
- `OpenAI.ApiKeyEnv` désigne la variable d’environnement qui contient la clé. Par défaut, le processus MCP lit `OPENAI_API_KEY`. Si le serveur local accepte les appels sans clé, laisse cette variable absente.
- `OpenAI.TimeoutSeconds` fixe le délai de chaque appel au LLM. `TokenLimitField` permet d’utiliser `max_tokens` ou `max_completion_tokens`. Active `UseJsonResponseFormat` si le serveur accepte `response_format: {"type":"json_object"}`.

Le fichier présenté par `--config` peut se trouver n’importe où. Les variables `RESEARCH_MCP_` surchargent ses valeurs, par exemple `RESEARCH_MCP_OpenAI__Model`. Les extraits lus et l’historique transmis sont envoyés au LLM configuré.

### Brancher un client MCP

Exemple de configuration générique. Adapte les chemins à ton installation.

```json
{
  "mcpServers": {
    "searxng-research": {
      "command": "dotnet",
      "args": [
        "E:/Développement/searxng-research-mcp/out/SearxngResearchMcp.dll",
        "--config",
        "E:/Développement/searxng-research-mcp/appsettings.local.json"
      ]
    }
  }
}
```

`research` attend le résultat dans le même appel MCP. Augmente le délai du client pour les recherches longues. Les modes `speed`, `balanced` et `quality` utilisent respectivement jusqu’à 1, 2 et 3 tours. Pour une question de suivi, transmets `history` avec les tours précédents (`question`, `answer`, `sources`). Chaque source d’un ancien tour contient `index`, `title` et `url`. Le serveur fonctionne sans base de données.

## Moteurs et modules optionnels

La [configuration SearXNG](deploy/searxng/settings.yml) reprend la liste de moteurs du module d’origine. Elle couvre le web généraliste, les actualités, la recherche scientifique, le code et les discussions. Les moteurs actifs varient selon leurs prérequis et l’instance. L’endpoint `/config` de SearXNG indique quels moteurs sont disponibles, et `web_search` indique lesquels ont échoué lors de la requête.

Pour personnaliser les moteurs, copie `deploy/searxng/settings.yml` dans `deploy/searxng-local/settings.yml`, ajoute `SEARXNG_CONFIG_DIR=./deploy/searxng-local` à `.env`, puis recrée le service SearXNG après tes modifications.

- **YaCy.** Démarre le service avec `docker compose --profile yacy up -d`. Ouvre l’interface sur `http://127.0.0.1:18090`, configure ton index et alimente-le. Dans la copie locale de `settings.yml`, passe le moteur `yacy` à `inactive: false`. Il interroge l’index local persistant. [Configuration du moteur YaCy](https://docs.searxng.org/dev/engines/online/yacy.html)
- **Ahmia/Tor.** Renseigne `Search.TorProxyUrl` avec le proxy SOCKS5 accessible au MCP. Dans la copie de `settings.yml`, passe `ahmia` à `inactive: false` et configure `proxies` (`all://` vers l’adresse `socks5h://…` accessible depuis SearXNG) et `using_tor_proxy: true` sous le moteur Ahmia. Recherche avec la catégorie `onion`. Le MCP filtre les résultats `.onion` tant que la liste de blocage publique d’Ahmia est indisponible. [Réglages par moteur SearXNG](https://docs.searxng.org/admin/settings/settings_engines.html)
- **Pages JavaScript.** Le profil `browser` démarre Playwright sur un réseau Docker interne. Le navigateur passe par le proxy filtrant du MCP. Lance `docker compose --profile browser up -d searxng playwright`, puis `docker compose --profile browser build mcp`. Dans le client MCP, utilise `docker` avec les arguments du bloc ci-dessous. Utilise une instance MCP à la fois avec ce profil. Pour un LLM qui tourne sur l’hôte Docker Desktop, utilise `host.docker.internal` dans `OpenAI.BaseUrl` et rends l’API accessible depuis Docker. Place la clé, si nécessaire, dans le fichier local ignoré `.env.llm`.

```json
["compose", "--project-directory", "E:/Développement/searxng-research-mcp", "--profile", "browser", "run", "--rm", "-T", "--use-aliases", "mcp"]
```

## Tests

```powershell
dotnet test tests/SearxngResearchMcp.Tests.csproj
dotnet build
python tests/smoke_mcp.py
```

Le test MCP utilise une API OpenAI simulée et lit une page publique de l’IANA. Définis `SEARXNG_SMOKE_URL=http://127.0.0.1:18888` pour utiliser aussi le service SearXNG réel lors du test de `web_search`.

[Licence MIT](LICENSE) · [Provenance et licences des dépendances](THIRD_PARTY_NOTICES.md)
