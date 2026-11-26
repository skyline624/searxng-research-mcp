# SearXNG Research MCP

Serveur MCP à démarrer avec **un seul conteneur Docker**. L’image contient le code .NET 10 ainsi que SearXNG, l’index local YaCy, Tor et Playwright/Chromium. Tous démarrent automatiquement. Les indexeurs distants et le réseau Tor sont interrogés à la demande. Le LLM, utilisé par `research`, est fourni par une API compatible OpenAI.

| Outil | Fonction |
| --- | --- |
| `web_search` | Renvoie les liens, les extraits et les moteurs indisponibles. Filtres par langue, catégorie, moteur, période et page. La catégorie `onion` interroge Ahmia via Tor. |
| `fetch_page` | Lit une URL publique HTTP(S), un PDF ou une URL `.onion`. Renvoie le texte ou le Markdown. Playwright rend les pages web JavaScript sur demande ou quand le texte initial est trop court. Les pages `.onion` sont lues via le proxy SOCKS5 local de Tor. |
| `research` | Planifie les requêtes, lit les sources, affine la recherche, classe les passages et rédige une réponse avec des citations numérotées. |

## Démarrage

```sh
git clone https://github.com/skyline624/searxng-research-mcp.git
cd searxng-research-mcp
docker compose up -d --build
docker compose logs -f mcp
```

Le volume Docker conserve l’index YaCy, l’état de Tor et le secret SearXNG, généré au premier démarrage. Seul le port MCP est publié sur la boucle locale. Tu peux changer son port hôte en définissant `MCP_PORT` dans `.env`.

Dans le client MCP, ajoute l’URL **`http://127.0.0.1:8765/mcp`** avec le transport **Streamable HTTP**. Pour un client local qui accepte la forme suivante :

```json
{
  "mcpServers": {
    "searxng-research": {
      "type": "http",
      "url": "http://127.0.0.1:8765/mcp"
    }
  }
}
```

Compose accorde `NET_ADMIN` pour installer le pare-feu dans le conteneur. Playwright s’exécute sous un utilisateur dédié. Ses seules nouvelles connexions réseau passent par le proxy filtrant local du MCP, qui vérifie les adresses publiques. Les autres services s’exécutent également sous des utilisateurs dédiés. Le serveur demande que la liste de blocage d’Ahmia ait été chargée avant de renvoyer ou de lire des URL `.onion`.

### Configurer le LLM

Pour Docker, crée un fichier local `.env.llm` avec les valeurs de ton API, puis lance `docker compose up -d`.

```dotenv
RESEARCH_MCP_OpenAI__BaseUrl=http://host.docker.internal:11434/v1
RESEARCH_MCP_OpenAI__Model=identifiant-du-modele
```

Si l’API exige une clé, fournis aussi `OPENAI_API_KEY` dans ce fichier local. Si son certificat HTTPS est signé par une autorité privée ou autosigné, place le **certificat public** au format PEM dans `certificates/` avec l’extension `.crt`, puis recrée le conteneur. Le certificat doit couvrir le nom ou l’adresse utilisé dans `BaseUrl`. Les extraits lus et l’historique transmis sont envoyés au LLM configuré.

- `RESEARCH_MCP_OpenAI__AnalysisModel` peut remplacer le modèle pour la planification, le classement des candidats et les tours de recherche.
- `RESEARCH_MCP_OpenAI__AnswerModel` peut remplacer le modèle de rédaction.
- `RESEARCH_MCP_OpenAI__TimeoutSeconds` règle le délai par appel. `RESEARCH_MCP_OpenAI__TokenLimitField` accepte `max_tokens` ou `max_completion_tokens`. `RESEARCH_MCP_OpenAI__UseJsonResponseFormat=true` demande des objets JSON structurés à une API compatible.

`research` attend le résultat dans le même appel. Les modes `speed`, `balanced` et `quality` utilisent jusqu’à 1, 2 et 3 tours. Pour une question de suivi, transmets `history` avec les tours précédents (`question`, `answer`, `sources`). Chaque source d’un ancien tour contient `index`, `title` et `url`.

### Moteurs et Tor

La [configuration SearXNG](deploy/searxng/settings.yml) est embarquée dans l’image. Elle couvre le web généraliste, les actualités, la recherche scientifique, le code, les discussions, YaCy et Ahmia. Pour demander un moteur précis, passe son nom à `web_search`. Par défaut, YaCy interroge son index local et les pairs de son réseau. Ahmia peut répondre lentement et dépend de son service distant et du réseau Tor. [API SearXNG](https://docs.searxng.org/dev/search_api.html)

### Mode stdio pour le développement

Le binaire .NET reste utilisable via stdio à partir de l’hôte si tu fournis les services dont tu as besoin. Copie `appsettings.example.json`, adapte ses URL, puis lance `dotnet run -- --config ./appsettings.local.json`.

## Tests

```sh
dotnet test tests/SearxngResearchMcp.Tests.csproj
dotnet build
python tests/smoke_mcp.py
python tests/smoke_docker.py
```

L’option `--research` dans `smoke_docker.py` teste le vrai LLM configuré. L’option `--onion-url` accepte l’adresse d’une page publique `.onion` connue et teste aussi Ahmia. Il faut laisser Tor établir son premier circuit avant ce test.

[Licence MIT](LICENSE) · [Provenance et licences des dépendances](THIRD_PARTY_NOTICES.md)
