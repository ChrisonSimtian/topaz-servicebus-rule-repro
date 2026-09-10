# Topaz — a subscription rule loses its filter

Minimal reproduction for [Topaz](https://github.com/TheCloudTheory/Topaz): a Service Bus subscription rule is
created successfully but its **filter definition is discarded**, so the topic cannot route by it.

Reproduced on `thecloudtheory/topaz-host:nightly`, **v1.11.146+0ab5c7cf5f** (image built 2026-09-09).

## What happens

```
PUT  {"properties":{"filterType":"CorrelationFilter","correlationFilter":{"properties":{"PayloadType":"Wanted"}}}}
GET  {"filterType":"SqlFilter"}
```

HTTP 200 both ways. The rule resource exists with the right name, type and parent path — but `filterType` has
been coerced to `SqlFilter` and there is no filter body at all.

The same happens for every shape we tried:

| Sent | Stored |
|---|---|
| `CorrelationFilter` with `properties` | `{"filterType":"SqlFilter"}` |
| `CorrelationFilter` with a native `label` | `{"filterType":"SqlFilter"}` |
| `SqlFilter` with a `sqlExpression` | `{"filterType":"SqlFilter"}` |

…on both `api-version=2024-01-01` and `api-version=2022-10-01-preview`.

The auto-created `$Default` rule *does* come back as `{"filterType":"True","action":{…}}`, so a filter can be
represented internally — it is the supplied one that is dropped.

## What Azure does

Azure returns the rule as sent: `filterType` stays `CorrelationFilter`, and `correlationFilter` round-trips
with its `properties` / `label`. A subscription with that rule then receives only matching messages.

## What works fine

Worth saying, because the problem is narrow:

- Namespace, topic and subscription are all modelled correctly — right resource types, right parent-child
  paths, `GET .../topics/{t}/subscriptions` returns the subscription.
- `listKeys` returns a usable connection string.

Two smaller things noticed alongside:

- `topic.subscriptionCount` stays `0` after a subscription is created.
- `GET .../topics` on a namespace created as a side-effect of an ARM deployment returns *every* Service Bus
  entity in the namespace — subscriptions and rules included, each with its correct `type`. A namespace
  created by an explicit `PUT` lists correctly. Possibly related to nested-template handling
  ([#205](https://github.com/TheCloudTheory/Topaz/issues/205)).

## Running it

```bash
docker compose up -d          # Topaz on 8899 (ARM) and 5671 (Service Bus AMQP)
dotnet run --project src/Repro
```

Expected output today:

```
Defect 1 — a rule does not keep its filter
  PUT  {"properties":{"filterType":"CorrelationFilter","correlationFilter":{"properties":{"PayloadType":"Wanted"}}}}
  GET  {"filterType":"SqlFilter"}
  FAIL  filterType survives the round trip
  FAIL  the filter definition survives the round trip
```

### Verified end to end

With [TheCloudTheory/Topaz#313](https://github.com/TheCloudTheory/Topaz/pull/313) applied, this repro
passes in full — the filter round-trips *and* the topic routes:

```
sent     : Wanted, Unwanted
received : Wanted
PASS  only Wanted is delivered
```

Note the repro deletes the subscription's `$Default` TrueFilter before sending. A subscription is created
with one, and while it is present every message matches through it regardless of any filter you add — the
same as Azure.

### The data-plane half needs the certificate trusted

The routing check sends two messages and expects only the matching one back. It connects over AMQP, and the
Azure SDK offers no certificate-validation hook for that transport, so Topaz's self-signed certificate has to
be trusted first:

```powershell
# elevated, one-time
Import-Certificate -FilePath .\certs\topaz.crt -CertStoreLocation Cert:\LocalMachine\Root
```

Without it the check reports `AuthenticationException: … UntrustedRoot` rather than a routing result.

## Why this matters

A topology where every subscription selects on a correlation filter cannot be emulated: with no filter
stored, a message either reaches every subscription or none. Because the control-plane calls all return
**HTTP 200**, a smoke test that asserts status codes passes while nothing routes.

Related: [#165](https://github.com/TheCloudTheory/Topaz/issues/165) requested exactly this and is closed
against milestone v1.7-beta, so this looks like a regression or an incomplete path rather than a missing
feature.
