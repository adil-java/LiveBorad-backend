# LiveBoard Backend

The API and real-time server for **LiveBoard**, a collaborative workspace where a team shares Kanban boards, chats and draws on a whiteboard together, and every change shows up for everyone instantly.

Built with **.NET 10**, **ASP.NET Core minimal APIs**, **SignalR** and **MongoDB**. Users sign in with **Firebase Authentication**.

The web app lives in [LiveBorad-frontend](https://github.com/adil-java/LiveBorad-frontend).

## Features

- **Boards and columns:** each new board starts with *To do*, *In progress* and *Done*. Columns can be added, renamed and removed.
- **Cards:** create, edit, reorder and move between columns. If two people save the same card at once, the second gets the first person's version back instead of silently overwriting it.
- **Team access:** invite people by email as **Viewer**, **Editor** or **Owner**. Invites to people without an account are kept and accepted when they sign in with that verified email.
- **Live updates:** every change is pushed to everyone viewing the board, along with who's online and who's editing which card.
- **Chat:** a chat per board with history, a "typing…" indicator and message deletion.
- **Whiteboard:** a shared drawing per board. Lines appear on other screens while they are being drawn and are saved when finished.
- **Activity feed:** a history of what changed on the board and who changed it.

## Tech stack

| Area | Technology |
|---|---|
| API | ASP.NET Core minimal APIs (.NET 10), OpenAPI |
| Real time | SignalR (WebSockets, with fallbacks) |
| Database | MongoDB (`MongoDB.Driver`) |
| Auth | Firebase ID tokens, validated with the standard JWT bearer handler (no service account needed) |
| Tests | xUnit, `WebApplicationFactory`, EphemeralMongo (a throwaway MongoDB for each test run) |

## How it works

Every change that is saved goes through the REST API. The API checks the caller's role, applies the business rules, saves to MongoDB, then broadcasts an event over SignalR to everyone else on the board. The SignalR hub only carries short-lived signals: who's online, who's editing, who's typing, and lines being drawn.

```
Client ──REST──▶ role check ──▶ domain rules ──▶ MongoDB
                                             └─▶ SignalR event ──▶ other clients
```

- **No echoes:** clients send their SignalR connection ID in the `X-SignalR-Connection-Id` header, so the author doesn't receive their own change twice.
- **Conflicts:** cards carry a version number. A stale save gets **409 Conflict** with the current card.
- **Card order:** cards use fractional positions, so moving a card updates a single document.
- **Privacy:** non-members get **404** for a board, so board IDs can't be guessed.

## Getting started

**Prerequisites:** [.NET 10 SDK](https://dotnet.microsoft.com/download), a MongoDB database (e.g. MongoDB Atlas), and a Firebase project.

1. **Configure secrets.** They are kept out of the repository with .NET user-secrets:
   ```bash
   dotnet user-secrets set "Mongo:ConnectionString" "mongodb+srv://<user>:<password>@<cluster>/" --project src/LiveBoard.Api
   dotnet user-secrets set "Firebase:ProjectId" "<your-firebase-project-id>" --project src/LiveBoard.Api
   ```
2. **Run:**
   ```bash
   dotnet run --project src/LiveBoard.Api
   ```
   The API listens on `http://localhost:5024`. Check `http://localhost:5024/health`, and see every endpoint at `http://localhost:5024/openapi/v1.json` (Development only).
3. **Test:**
   ```bash
   dotnet test
   ```
   The first run downloads a temporary MongoDB binary, so no database setup is needed for tests.

## Configuration

| Setting | Required | Description |
|---|---|---|
| `Mongo:ConnectionString` | Yes | MongoDB connection string. Keep it in user-secrets or an environment variable. |
| `Mongo:Database` | Yes | Database name. Defaults to `liveboard`, or `liveboard-dev` in Development. |
| `Firebase:ProjectId` | Yes | Firebase project ID; only tokens issued for this project are accepted. |
| `Cors:AllowedOrigins` | For browsers | Exact frontend origins, e.g. `https://liveboard.example.com`. `http://localhost:5173` is allowed in Development. |
| `PORT` | No | When set (as on Render and similar hosts), the API listens on `0.0.0.0:$PORT`. |

As environment variables, use double underscores: `Mongo__ConnectionString`, `Firebase__ProjectId`, `Cors__AllowedOrigins__0`.

The app checks this configuration at startup and refuses to start if a required value is missing.

## API overview

All routes are under `/api` and need a Firebase ID token: `Authorization: Bearer <token>`.

| Area | Routes |
|---|---|
| Profile | `POST /me/sync` creates or refreshes your profile and accepts pending invites. Clients call it after each sign-in. |
| Boards | `GET` / `POST /boards`, `GET` / `PATCH` / `DELETE /boards/{boardId}` |
| Columns | `POST /boards/{id}/columns`, `PATCH` / `DELETE /boards/{id}/columns/{columnId}` |
| Cards | `POST /boards/{id}/cards`, `PUT` / `DELETE /boards/{id}/cards/{cardId}`, `POST /boards/{id}/cards/{cardId}/move` |
| Members | `GET` / `POST /boards/{id}/members`, `PATCH` / `DELETE /boards/{id}/members/{userId}`, `DELETE /boards/{id}/members/invites/{email}` |
| Activity | `GET /boards/{id}/activity?before=&limit=` |
| Chat | `GET` / `POST /boards/{id}/chat`, `DELETE /boards/{id}/chat/{messageId}` |
| Whiteboard | `GET` / `DELETE /boards/{id}/drawing`, `POST /boards/{id}/drawing/strokes`, `POST /boards/{id}/drawing/erase` |

Errors come back as standard problem responses (`application/problem+json`): **400** invalid input, **403** role too low, **404** not found or not a member, **409** someone else changed it first.

### Real-time hub: `/hubs/board`

Browsers pass the token as `?access_token=` because WebSockets can't send headers.

- **Client → server:** `JoinBoard(boardId)`, `LeaveBoard()`, `StartEditing(cardId)`, `StopEditing(cardId)`, `Typing()`, `DrawProgress(...)`
- **Server → client:** board, column, card, member, presence, activity, chat and whiteboard events. The full typed list is in [`Realtime/IBoardClient.cs`](src/LiveBoard.Api/Realtime/IBoardClient.cs).

### Roles

| | Viewer | Editor | Owner |
|---|:-:|:-:|:-:|
| View the board, chat, whiteboard and activity | ✓ | ✓ | ✓ |
| Send chat messages | ✓ | ✓ | ✓ |
| Edit cards and columns, draw | | ✓ | ✓ |
| Rename or delete the board, manage members | | | ✓ |

## Project structure

```
src/
  LiveBoard.Domain/     Entities and business rules (boards, cards, ranking, chat, strokes); no database or web code
  LiveBoard.Api/
    Auth/               Firebase token validation and board role checks
    Configuration/      Typed settings
    Data/               MongoDB collections, indexes and repositories
    Features/           REST endpoints per feature, request/response contracts
    Realtime/           SignalR hub, event definitions, presence tracking
    Infrastructure/     Maps domain errors to HTTP responses
tests/
  LiveBoard.Domain.Tests/   Unit tests for business rules
  LiveBoard.Api.Tests/      Integration tests: real API, temporary MongoDB, SignalR clients
```

## Deployment notes

- Set the configuration above as environment variables on your host. Never commit the connection string.
- Add the deployed frontend URL to `Cors:AllowedOrigins`.
- Who's online is tracked in memory, which works for a single API instance. Running several instances needs a SignalR backplane such as Redis.
