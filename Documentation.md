# Documentation

## Table of Contents

- [Installation](#installation)
- [Configuration](#configuration)
- [Endpoints](#endpoints)
- [Match State Schema](#match-state-schema)
- [Player State Schema](#player-state-schema)
- [Actions Reference](#actions-reference)
- [SSE Events Reference](#sse-events-reference)
- [Error Responses](#error-responses)
- [Examples](#examples)

## Installation

Follow [this guide](https://gurrenm3.github.io/BTD-Mod-Helper/wiki/Install-Guide) to install MelonLoader and BTD Mod Helper, then drop the mod's `.dll` in your Mods folder. The server will launch automatically on startup.

---

## Configuration

| Setting | Default | Range | Description |
|---|---|---|---|
| `HTTP Server Port` | `5001` | 1024–65535 | Port the server listens on. Changes take effect immediately without restarting the game. |

Settings are found in the BTD6 Mod Helper menu (Main Menu → Mods → BTD6 HTTP API → Settings).

---

## Endpoints

The base URL is `http://localhost:<port>/` (see config above). All responses have `Content-Type: application/json` and include the header `Access-Control-Allow-Origin: *`.

### GET /match

Returns the current match state.

**Not in a match:**

```json
{ "status": "notInMatch" }
```

**In a match:** see [Match State Schema](#match-state-schema) for the full shape.

---

### GET /player

Returns player profile data. See [Player State Schema](#player-state-schema).

---

### GET /health

Liveness check. Always returns:

```json
{ "ok": true }
```

---

### POST /action

Sends a command to the game. The request body must be a JSON object with at least a `"type"` field; additional fields depend on the action type.

```
Content-Type: application/json
```

**Success response**

```json
{ "ok": true }
```

See [Actions Reference](#actions-reference) for every supported action and its required fields.

---

### GET /events (SSE)

Opens a persistent [Server-Sent Events](https://developer.mozilla.org/en-US/docs/Web/API/Server-sent_events/Using_server-sent_events) stream. The connection stays open and the server pushes named events as they happen in-game.

**Response headers**

```
Content-Type: text/event-stream
Cache-Control: no-cache
X-Accel-Buffering: no
```

**Event wire format**

```
event: <eventName>
data: <JSON payload>

```

Each event ends with a blank line. See [SSE Events Reference](#sse-events-reference) for every event name and its payload.

If you disconnect from the client side the server will clean up the slot automatically.

---

## Match State Schema

Returned by `GET /match` when a match is active (`"status": "inMatch"`).

```jsonc
{
  "status": "inMatch",

  // Map / mode
  "map": "string",               // Internal map name, e.g. "InTheLoop"
  "difficulty": "string",        // e.g. "Medium"
  "gameMode": "string",          // e.g. "Standard", "Deflation"
  "gameType": "string",          // e.g. "Standard", "Odyssey"

  // Time
  "isPaused": false,
  "isFastForwarding": false,
  "isAutostartEnabled": false,

  // Economy
  "cash": 650.0,
  "cashSpent": 3500.0,
  "lives": 150.0,
  "maxLives": 200.0,

  // Rounds
  "round": 62,                    // Current round (0-indexed)
  "endRound": 79,
  "roundIsRunning": false,

  // Towers
  "towers": [ /* ... */ ],        // Array of tower objects; see below
  "selectedHero": "string",       // Internal hero ID, e.g. "Quincy"

  // Available towers (towers that can be placed this match, excluding the hero)
  "availableTowers": [ "DartMonkey", "BombShooter", /* ... */ ]
}
```

#### Tower object

```jsonc
{
  "id": 123,                      // Numeric ID
  "type": "string",               // Tower model name, e.g. "DartMonkey"
  "upgrades": [0, 0, 0],          // [top, middle, bottom] upgrade tiers
  "pos": { "x": 0.0, "y": 0.0 },
  "roundPlaced": 12,              // Round the tower was placed (0-indexed)
  "sellValue": 120,
  "cashEarned": 0.0,
  "livesGained": 0.0,
  "isHero": false,
  "targetingMode": "string",
  "canTargetCamo": false,
  "canUpgradeToParagon": false,
  "isParagon": false,
  "pops": 0,
  "damage": 0.0,
  "necroBloonsReanimated": 0,

  // Abilities (empty array for towers without abilities)
  "abilities": [
    {
      "name": "string",
      "isReady": true,
      "cooldown": 45.0,             // Total ability cooldown, in seconds
      "cooldownRemaining": 3.2,
      "isActive": false,
      "additionalCharges": 0,
      "isPassive": false,
      "activateOnLeak": false,
      "activateOnPreLeak": false,
      "activateOnLivesLost": false,
      "activateOnRoundEnd": false
    }
  ]
}
```

---

## Player State Schema

Returned by `GET /player`.

```jsonc
{
  "rank": 155,
  "veteranRank": 12,
  "monkeyMoney": 48200,
  "knowledgePoints": 3,
  "acquiredKnowledge": [ "CheapRangs", /* ... */ ],  // Internal knowledge IDs
  "isKnowledgeEnabled": true
}
```

---

## Actions Reference

All actions are sent as `POST /action` with a JSON body. Actions that affect towers or abilities require an active match; the server returns `{ "error": "not in match" }` otherwise.

### Time

#### `setFastForward`
Toggle fast-forward mode.
```json
{ "type": "setFastForward", "enabled": true }
```

#### `setFastForwardSpeed`
Override the fast-forward time scale multiplier.
```json
{ "type": "setFastForwardSpeed", "speed": 5.0 }
```

#### `setAutostart`
Enable or disable autostart.
```json
{ "type": "setAutostart", "enabled": true }
```

#### `setPaused`
Pause or unpause the game. *Note: Does not open the pause menu UI.*
```json
{ "type": "setPaused", "paused": true }
```

---

### Game Flow

#### `startRound`
Manually start the next round (equivalent to pressing the Play button).
```json
{ "type": "startRound" }
```

#### `setRound`
Jump to a specific round number.
```json
{ "type": "setRound", "round": 50 }
```

#### `restart`
Restart the current match.
```json
{ "type": "restart" }
```

---

### Economy

#### `setCash`
Set cash to an exact amount.
```json
{ "type": "setCash", "value": 10000 }
```

#### `addCash`
Add (or subtract, with a negative value) cash.
```json
{ "type": "addCash", "value": 500 }
```

#### `setLives`
Set lives to an exact amount.
```json
{ "type": "setLives", "value": 200 }
```

#### `addLives`
Add (or subtract) lives.
```json
{ "type": "addLives", "value": 50 }
```

---

### Towers

Tower `id` values can be found in the `GET /match` response.

#### `placeTower`
Place a tower at a world-space position. Set `free` to `true` to ignore cash (equivalent to using an insta).
```json
{
  "type": "placeTower",
  "towerId": "DartMonkey",
  "x": 10.5,
  "y": -3.2,
  "free": false
}
```
Returns `{ "error": "tower not found" }` if `towerId` doesn't match any known tower ID.

#### `upgradeTower`
Upgrade a tower along a given path (0 = top, 1 = middle, 2 = bottom).
```json
{ "type": "upgradeTower", "id": 123456, "path": 0 }
```

#### `sellTower`
Sell a tower for its current sell value.
```json
{ "type": "sellTower", "id": 123456 }
```

#### `changeTargetPriority`
Cycle the targeting mode of a tower. Pass `prev: true` to cycle backwards.
```json
{ "type": "changeTargetPriority", "id": 123456, "prev": false }
```

#### `activateAbility`
Activate a named ability on a specific tower.
```json
{ "type": "activateAbility", "id": 123456, "abilityName": "Summon Phoenix" }
```
`id` is the tower ID. `abilityName` must match the `abilities[].name` string from the match state exactly.

Returns `{ "error": "ability not found" }` if no matching ability exists on that tower.

#### `activateAllAbilities`
Activate every ability on every tower simultaneously.
```json
{ "type": "activateAllAbilities" }
```

---

## SSE Events Reference

Connect to `GET /events` to receive these events as they happen.

| Event | Payload | Description |
|---|---|---|
| `matchStart` | `{}` | A new match has started. |
| `roundStart` | `{}` | A round has begun. |
| `roundEnd` | `{}` | The round has ended. |
| `victory` | `{}` | The player has won the match. |
| `defeat` | `{}` | The player has lost the match. |
| `restart` | `{}` | The match was restarted. |
| `mainMenu` | `{}` | The player returned to the main menu. |
| `rankUp` | `{}` | The player's rank increased. |
| `towerPlaced` | `{ "id": number }` | A tower was placed. `id` is its numeric tower ID. |
| `towerUpgraded` | `{ "id": number }` | A tower was upgraded. |
| `towerSold` | `{ "id": number, "value": number }` | A tower was sold. `value` is the cash returned. |
| `heroLeveledUp` | `{ "id": number }` | The hero leveled up. |
| `abilityActivated` | `{ "towerId": number, "abilityName": "string" }` | An ability was activated. |
| `abilityReady` | `{ "towerId": number, "abilityName": "string" }` | An ability came off cooldown and is ready to use. |
| `bloonLeak` | `{ "type": "string", "damage": number }` | A bloon leaked. `type` is the bloon model name; `damage` is lives lost. |

---

## Error Responses

| Scenario | HTTP status | Body |
|---|---|---|
| Unknown path | 404 | `{ "error": "not found" }` |
| Unknown action `type` | 200 | `{ "error": "unknown action: <type>" }` |
| Action called outside a match | 200 | `{ "error": "not in match" }` |
| Action-specific error | 200 | See individual action docs above |
| Unhandled exception | 200 | `{ "error": "<exception message>" }` |

---

## Examples

### Poll match state (Python)

```python
import requests

state = requests.get("http://localhost:5001/match").json()
if state["status"] == "inMatch":
    print(f"Round {state['round']} | Cash: {state['cash']} | Lives: {state['lives']}")
```

### Read player profile (Python)

```python
import requests

player = requests.get("http://localhost:5001/player").json()
print(f"Rank {player['rank']} | Monkey Money: {player['monkeyMoney']}")
```

### Send an action (Python)

```python
import requests

requests.post("http://localhost:5001/action", json={
    "type": "placeTower",
    "towerId": "DartMonkey",
    "x": 10.5,
    "y": -3.2,
    "free": True
})
```

### Listen to events (Python)

```python
import requests
import sseclient

response = requests.get(
    "http://localhost:5001/events",
    stream=True,
    headers={"Accept": "text/event-stream"},
)
client = sseclient.SSEClient(response)

for event in client.events():
    print(f"Event: {event.event} | Data: {event.data}")
```

### Listen to events (JavaScript)

```javascript
const es = new EventSource("http://localhost:5001/events");

es.addEventListener("roundStart",   () => console.log("Round started!"));
es.addEventListener("abilityReady", e => console.log("Ability ready:", JSON.parse(e.data)));
es.addEventListener("bloonLeak",    e => console.log("Leak:", JSON.parse(e.data)));
es.addEventListener("victory",      () => { console.log("Victory!"); es.close(); });
```