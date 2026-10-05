# BTD6 v56 Compatibility Update

This fork updates `btd6-http-api` to compile and run with the current BTD6 API.

## Problem

The original project targeted BTD6 v54. Newer BTD6 versions changed several methods in `UnityToSimulation` and `AbilityToSimulation`.

The main change was that many simulation actions now require an explicit `inputId` for local players.

Previously, methods could be called like:

```csharp
bridge.SellTower(towerId);
```

The current API requires:

```csharp
bridge.SellTower(bridge.GetInputId(), towerId);
```

The input ID identifies which local player/input is issuing the simulation action.

## Changes Made

### Ability retrieval

Changed:

```csharp
bridge.GetAllAbilities(false)
```

to:

```csharp
bridge.GetAllAbilities(bridge.GetInputId(), false)
```

The current API requires the player's input ID when retrieving abilities.

### Player identification

Replaced:

```csharp
bridge.MyPlayerNumber
```

with:

```csharp
bridge.GetInputId()
```

`MyPlayerNumber` is no longer available in the current API.

### Tower placement

Updated `CreateTowerAt` to provide:

- `bridge.GetInputId()`
- the required success callback

Example:

```csharp
bridge.CreateTowerAt(
    bridge.GetInputId(),
    position,
    model,
    ObjectId.FromData(0),
    free,
    new Action<bool>(success => { })
);
```

### Tower upgrades

Updated `UpgradeTower` to provide:

- `bridge.GetInputId()`
- the required success callback

### Tower selling

Changed:

```csharp
bridge.SellTower(towerId);
```

to:

```csharp
bridge.SellTower(bridge.GetInputId(), towerId);
```

### Tower targeting

Updated `SetNextTowerTargetType` to provide the player's input ID.

### Ability activation

Changed:

```csharp
ability.Activate();
```

to:

```csharp
ability.Activate(bridge.GetInputId());
```

## Result

After these changes:

- The project builds successfully against the current BTD6 assemblies.
- The HTTP server starts successfully.
- `/health` works.
- `/match` successfully returns current match information.

The HTTP API architecture itself did not need to be rewritten. The compatibility changes were primarily updates to calls into BTD6's simulation API.
