# Documentație tehnică — NEW_STATISTIC

## 2026-04-10 — PersistenceHostedService: fără PeriodicTimer în cursă cu `WaitToReadAsync`

### Modificări

- **`PersistenceHostedService`**: `PeriodicTimer.WaitForNextTickAsync` nu poate fi apelat în paralel pe același timer; bucla folosea `Task.WhenAny(read, tick)` și pornea un **nou** tick înainte ca apelul anterior să se termine dacă câștiga `read` → **`InvalidOperationException`** și oprire host (**`BackgroundServiceExceptionBehavior = StopHost`**). Înlocuit cu **`Task.Delay(flushMs)`** + **`CancellationTokenSource`** legat de `stoppingToken`, cu anularea operației pierzătoare după `WhenAny`.

## 2026-04-10 — Binance USD futures: subscribe în batch-uri (limită ~4000 B)

### Modificări

- **`TradingOptions`**: proprietate nouă **`BinanceSubscribeSymbolsBatchSize`** (implicit **80**) — Binance respinge un singur mesaj de subscribe prea mare; simbolurile sunt trimise în mai multe apeluri `SubscribeToAggregatedTradeUpdatesAsync`.
- **`BinanceUsdFuturesAggregateTradeSource`**: împarte lista de simboluri cu **`Chunk(batchSize)`**; log per batch și la final „all batches active”; handler-ul de trade este în **`try/catch`** ca excepțiile din callback să nu oprească socket-ul.
- **`NEW_STATISTIC.Worker/appsettings.json`**: exemplu **`BinanceSubscribeSymbolsBatchSize`: 80** (dacă apare din nou eroarea de mărime, micșorați la 50–60).
- **`MarketPipelineHostedService`**: canalul spre pipeline are **`SingleWriter = false`** — cu mai multe batch-uri Binance, **mai multe thread-uri** apelează **`TryWrite`** simultan; **`SingleWriter = true`** încălca contractul `Channel` și putea cauza crash / comportament nedefinit.

## 2026-04-10 — WebSocket: doar Worker; mesaje explicite în consolă

### Comportament

- **WebSocket-ul către bursă** (Binance / Bybit) rulează **doar** în **`NEW_STATISTIC.Worker`**. **`NEW_STATISTIC.Api`** servește HTTP + frontend; **nu** deschide conexiune la schimb pentru trade-uri live.
- Pentru date live și loguri de tip „connected / trades”, rulați Worker: `dotnet run --project NEW_STATISTIC.Worker` (ideal **în paralel** cu Api dacă doriți UI + DB).

### Modificări

- **`NEW_STATISTIC.Worker/Program.cs`**: la pornire, log **Information** care explică că acest proces deschide WebSocket-ul și că Api nu.
- **`NEW_STATISTIC.Worker/MarketPipelineHostedService`**: log la pornirea pipeline-ului; la **primul** trade care trece prin canal — log **Information** (confirmă capăt-la-capăt).
- **`NEW_STATISTIC.Api/Program.cs`**: după migrări, log **Information** că Api este doar HTTP și că trade-urile live sunt în Worker.

## 2026-04-10 — Structură monorepo (backend)

### Modificări

- **`NEW_STATISTIC.sln`**: calea proiectului a fost actualizată la `backend\NEW_STATISTIC.csproj`, astfel încât soluția deschide proiectul real.
- **Configurație ASP.NET Core mutată în `backend/`**:
  - `Properties/launchSettings.json` — profiluri de rulare (http/https, porturi).
  - `appsettings.Development.json` — logging în mediul Development.
  - `NEW_STATISTIC.http` — cereri HTTP de test (același host ca în launchSettings).
- **Eliminat din rădăcina repo-ului**: duplicatele acestor fișiere (`Properties/`, `appsettings.Development.json`, `NEW_STATISTIC.http`) pentru a evita confuzia între „proiect la root” și proiectul din `backend`.
- **Curățat**: directoarele `bin/` și `obj/` din rădăcină (artefacte vechi de build când `.csproj` părea la root); build-ul curent se generează sub `backend/bin` și `backend/obj`.

### Structură recomandată după schimbare

- Rădăcină: `NEW_STATISTIC.sln`, `DOCUMENTATIE_TEHNICA.md`, `frontend/` (client).
- `backend/`: aplicația web .NET (`NEW_STATISTIC.csproj`, `Program.cs`, `appsettings*.json`, `Properties/`, `NEW_STATISTIC.http`).

### Rulare

Din folderul `backend`:

```bash
dotnet run
```

Sau deschideți soluția în IDE și rulați proiectul **NEW_STATISTIC** (calea `backend\NEW_STATISTIC.csproj`).

## 2026-04-10 — `.gitignore`, frontend servit de API, CORS (Development)

### Modificări

- **`.gitignore` (rădăcină repo)**: ignoră `bin/`, `obj/`, `.vs/`, fișiere utilizator IDE, artefacte NuGet comune, `node_modules/` / `dist/` pentru un eventual toolchain npm.
- **`backend/Program.cs`**:
  - dacă există folderul `../frontend` relativ la `ContentRoot`, API-ul servește fișiere statice de acolo (`UseDefaultFiles`, `UseStaticFiles`) și `MapFallbackToFile("index.html")` pentru rute nepotrivite (SPA-friendly);
  - în **Development**: `AddCors` / `UseCors` cu politică care permite origini `localhost` și `127.0.0.1` (ex. Live Server pe alt port), fără a schimba numele endpoint-ului existent `/weatherforecast`.
- **`frontend/`**: `index.html`, `app.js`, `style.css` — pagină minimală care apelează `GET /weatherforecast` și afișează rezultatul.

### Rulare cu UI

Din `backend`: `dotnet run`, apoi deschideți în browser URL-ul din `launchSettings` (ex. `http://localhost:5088/`). Rădăcina site-ului este frontend-ul; API rămâne pe aceeași origine.

### Notă

În **Production**, dacă nu există folderul `frontend` lângă deploy sau nu doriți servirea din `../frontend`, folosiți `wwwroot` în proiect sau un reverse proxy; CORS pentru Development nu este activ în alte medii decât dacă extindeți configurația.

## 2026-04-10 — Binance.Net (API Binance)

### Modificări

- **`backend/NEW_STATISTIC.csproj`**: adăugat pachetul NuGet **`Binance.Net`** (versiune **11.9.0**), cu dependența comună **`CryptoExchange.Net`** (versiunea efectivă din restore depinde și de celelalte pachete JKorf; cu **Bybit.Net** 6.x este **11.1.0**).
- **`backend/Program.cs`**: apel **`builder.Services.AddBinance()`** pentru înregistrarea în DI a clientului Binance (ex. `IBinanceRestClient` injectabil în servicii).

### Utilizare (scurt)

- Injectați **`Binance.Net.Interfaces.Clients.IBinanceRestClient`** (sau tipul concret din documentația pachetului) în constructorii serviciilor voastre.
- Pentru cereri private (cont, ordine): configurați chei API prin `AddBinance(options => …)` sau `IConfiguration` / user secrets — **nu** comitați chei în repo.
- Documentație pachet: [JKorf/Binance.Net](https://github.com/JKorf/Binance.Net) (GitHub).

## 2026-04-10 — Bybit.Net (API Bybit)

### Modificări

- **`backend/NEW_STATISTIC.csproj`**: adăugat pachetul NuGet **`Bybit.Net`** (versiune **6.11.0**). Restaurarea a aliniat dependența comună **`CryptoExchange.Net`** la **11.1.0** (folosită și de Binance.Net în soluția curentă; build verificat).
- **`backend/Program.cs`**: apel **`builder.Services.AddBybit()`** pentru înregistrarea clientului Bybit în DI (ex. injectare **`IBybitRestClient`** în servicii).

### Utilizare (scurt)

- Documentație și exemple: [JKorf/Bybit.Net](https://github.com/JKorf/Bybit.Net) (GitHub).
- Pentru API-uri private: configurați chei prin `AddBybit(options => …)` sau configurație securizată; **nu** comitați chei în repo.

## 2026-04-10 — SQLite (Entity Framework Core)

### Modificări

- **`backend/NEW_STATISTIC.csproj`**:
  - **`Microsoft.EntityFrameworkCore.Sqlite`** **10.0.0** — provider EF Core pentru SQLite (include **`Microsoft.Data.Sqlite.Core`** și **`Microsoft.EntityFrameworkCore`** prin dependențe).
  - **`Microsoft.EntityFrameworkCore.Design`** **10.0.0** — instrumente pentru migrații (`dotnet ef`); referință cu **`PrivateAssets=all`** (nu se publică odată cu aplicația).
- **`.gitignore`**: ignorate fișierele SQLite locale (`*.db`, `*.db-shm`, `*.db-wal`).

### Pași următori (în cod)

1. Definiți un **`DbContext`** și entități.
2. În **`Program.cs`**: `builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(connectionString));` cu șir din `appsettings.json` (ex. `Data Source=app.db`).
3. Migrații (din folderul `backend`): `dotnet tool install --global dotnet-ef` (dacă lipsește), apoi `dotnet ef migrations add InitialCreate` și `dotnet ef database update`.

Documentație: [EF Core cu SQLite](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/).

## 2026-04-10 — Implementare plan OOP: Core / Infrastructure / Worker / Api

### Modificări

- **Soluția** conține proiectele: **`NEW_STATISTIC.Core`** (modele, opțiuni, `SymbolPipeline`, buffer 5 min, trigger pe același ms, lumânare ±150 ms, follow-up, `SimulationEngine`), **`NEW_STATISTIC.Infrastructure`** (EF SQLite `StatisticDbContext`, entități `Candles` / `Simulations`, surse **Binance** USD futures agg trades și **Bybit** linear public trades, `ChannelPersistenceSink`, `EfCandlePersistence`), **`NEW_STATISTIC.Worker`** (Host: migrări DB, consumator coadă persistență, `MarketPipelineHostedService` → WebSocket → pipeline per simbol), **`NEW_STATISTIC.Api`** (REST `GET /api/candles`, `GET /api/candles/{id}/simulations`, fișiere statice din `../frontend`).
- **Eliminat** vechiul folder **`backend/`** (proiectul unic); pachetele **Binance.Net** (actualizat la **12.11.3** în Infrastructure pentru compatibilitate **CryptoExchange.Net 11.1.0**), **Bybit.Net 6.11.0**, **EF Core 10** sunt în **Infrastructure** / **Worker** / **Api** după caz.
- **Baza de date**: fișier partajat **`statistic.db`** în rădăcina repo-ului (cale rezolvată din `ContentRoot/../statistic.db` în **Worker** și **Api**). Migrație EF: **`NEW_STATISTIC.Infrastructure/Data/Migrations/InitialCreate`**. Comandă ulterioară: `dotnet ef migrations add ... --project NEW_STATISTIC.Infrastructure --startup-project NEW_STATISTIC.Worker`.
- **Config**: secțiunea **`Trading`** în `NEW_STATISTIC.Worker/appsettings.json` (`Exchange`, `TriggerDiffPercent`, `CandleHalfWindowMs`, `MaxSymbols`, parametri simulări, etc.).

### Rulare

1. **Worker** (procesare live + scriere DB): din repo — `dotnet run --project NEW_STATISTIC.Worker`.
2. **Api** + UI: `dotnet run --project NEW_STATISTIC.Api` — `http://localhost:5088/` (frontend + API).

### Note / limite actuale

- **Bybit**: stream-ul folosește **trade-uri publice** (nu agregate identic cu Binance); modelul intern este același.
- **Simulări**: rezultat per interval folosește min/max agregate pe interval; dacă TP și SL sunt ambei „atinse” în sensul extremelor, se compară timestamp-urile min/max (MVP).
- **Instrument `dotnet-ef`**: instalat global (`dotnet tool install --global dotnet-ef`).

## 2026-04-10 — Completări plan: reconectare WS, timeout lumânare, batch DB, health, teste

### Modificări

- **Reconectare WebSocket** (`BinanceUsdFuturesAggregateTradeSource`, `BybitLinearTradeSource`): buclă exterioară cu backoff exponențial (`WebSocketReconnectInitialMs`, `WebSocketReconnectMaxMs` în `TradingOptions`); sesiunea se reia la erori sau subscribe eșuat; oprire lină la `CancellationToken`.
- **Închidere lumânare**: `SymbolPipeline` ține `_maxTradeTimeMs`; pentru fiecare `PendingCandle`, după ce max time ≥ `WindowEnd` se pornește `StreamReachedWindowEndUtc`; dacă nu apare trade cu `T > WindowEnd`, după `CandleCloseWaitMs` (ms) se forțează închiderea (conform politicii din plan).
- **Agregare ms**: extrasă în `MillisecondBucketAggregator` (min/max per milisecundă).
- **Persistență**: `EfCandlePersistence.SaveBatchAsync` într-o tranzacție; `PersistenceHostedService` adună batch (`PersistenceBatchSize`, `PersistenceFlushIntervalMs`) și scrie în fundal.
- **Observabilitate**: `TradeIngestMetrics` + `TradeMetricsLoggingHostedService` (log / minut); API **`GET /health`** cu `StatisticDbHealthCheck` (SQLite).
- **API**: **`GET /api/candles/{id}`** — detaliu lumânare inclusiv `followUpJson`.
- **Teste**: proiect **`NEW_STATISTIC.Core.Tests`** (xUnit) — `CandleSideResolver`, `MillisecondBucketAggregator`, `SimulationEngine`.

## 2026-04-10 — Worker: logging vizibil în consolă (Debug)

### Modificări

- **`NEW_STATISTIC.Worker/appsettings.json`** și **`appsettings.Development.json`**: `Default` → **`Debug`**, **`Microsoft`** / **`Microsoft.EntityFrameworkCore`** / **`System`** → **`Warning`**, **`Microsoft.Hosting.Lifetime`** → **`Information`**, astfel **`LogDebug`** (ex. trade-uri WebSocket) apare în consolă și fără mediu Development. Pentru **Production** puteți reveni manual la **Information** pe `Default` dacă doriți mai puțin zgomot.

## 2026-04-10 — API: migrări SQLite la pornire

### Modificări

- **`NEW_STATISTIC.Api/Program.cs`**: după `Build()`, înainte de `UseCors` / endpoint-uri, se apelează **`Database.MigrateAsync()`** pe același fișier SQLite ca Worker (`ContentRoot/../statistic.db`). Astfel, dacă rulați doar **Api** (fără Worker), tabelele **`Candles`** / **`Simulations`** sunt create; nu mai apare eroarea SQLite **`no such table: Candles`**.
- **`NEW_STATISTIC.Api/appsettings.json`** și **`appsettings.Development.json`**: **`Microsoft.EntityFrameworkCore`** și **`Microsoft.EntityFrameworkCore.Database.Command`** la **`Warning`**, ca migrările și comenzile SQL să nu umple consola la nivel **Information** (mesajele aplicației rămân vizibile).

## 2026-04-10 — Logging WebSocket: conectare + tranzacții primite

### Modificări

- **`NEW_STATISTIC.Infrastructure/Exchange/BinanceUsdFuturesAggregateTradeSource.cs`**:
  - după **`SubscribeToAggregatedTradeUpdatesAsync` reușit**: log **Information** — WebSocket conectat / abonat, număr simboluri, **Subscription id** (`sub.Data?.Id`, 0 dacă lipsește);
  - în callback: la **primul** trade procesat — log **Information** (simbol, `timeMs`, preț, cantitate);
  - la fiecare **5000** de trade-uri în sesiune — log **Information** cu total cumulativ (eșantion, fără spam);
  - **LogDebug** pe fiecare trade (activ doar dacă nivelul de logging include `Debug`).
- **`NEW_STATISTIC.Infrastructure/Exchange/BybitLinearTradeSource.cs`**: aceeași logică pentru **`SubscribeToTradeUpdatesAsync`** (primul trade, rezumat la 5000, `LogDebug` per trade).
- La începutul fiecărei sesiuni WS se resetează contoarele sesiunii (`Interlocked`).
