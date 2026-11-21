# The TFM sample domain

Every example in TFM documentation, in any TFM library, is set in one fictional place: **the Portly
Primate Sanctuary**, a retirement home for overweight monkeys that runs on bananas and paperwork.

Use it for every code sample, XML comment example, test name that reads like a business and
screenshot. Do not invent a second domain, and never borrow names from a real application, site
or customer. One story told everywhere means a reader who has seen one library's docs already
knows the cast when they open the next.

The entities below exist as code in `tests/TFM.Nucleus.DocSamples/Entities.cs`, which is where a Nucleus
example comes from. Nothing here is real. If a name below ever collides with a real consumer's, change it here first.

## The cast

| Who | What to know |
|---|---|
| Gerald | Capuchin, 14.2 kg, `Mood.Smug`. Has never missed a meal. The default monkey in any example that needs one. |
| Brenda | Macaque, 11.8 kg, `Mood.Plotting`. The subject of most mischief reports. |
| Sir Reginald | Mandrill, 31.0 kg, `Mood.Asleep`. Use him when an example needs a large number. |
| Mavis | Head keeper, `mavis@portlyprimate.example`. Signs everything. |
| Colin | Trainee keeper, `colin@portlyprimate.example`. Use him for the row that fails validation. |

Email addresses always end in `.example`, which is reserved and can never be registered.

## The entities

| Entity | Members | Use it to show |
|---|---|---|
| `Monkey` | `Name`, `WeightKg`, `Mood` | The smallest root entity: the first example on any page. An enum column (`Mood`). A unique index (`Name`). |
| `Resident` | `Name`, `Keeper`, `TroopId`, `Naps` | A monkey in residence, for relationships: it has a keeper, belongs to a troop and logs its naps. |
| `Troop` | `Name`, `Motto`, `Residents` | Roots that belong to another root: a troop's residents, by `Resident.TroopId`. |
| `Keeper` | `Name`, `Email` | A reference from another entity (`Resident.Keeper`). A unique violation (`Email`). Sign-in. |
| `BananaVariety` | `Name`, `Curvature` | A small lookup table that is cached. |
| `BananaOrder` | `PlacedUtc`, `Status`, `Lines` | A parent with owned rows. A transaction. A confirmation email. A payment. |
| `BananaOrderLine` | `Variety`, `Quantity`, `Substitutions` | A child row with a rule to validate (`Quantity` above zero). |
| `LineSubstitution` | `Variety`, `Reason` | A grandchild: owned rows two levels down. |
| `NapLog` | `StartedUtc`, `Minutes`, `Location` | A child collection that grows large, so it is loaded on demand. Grouped and counted reads. |
| `WeighIn` | `MonkeyName`, `WeightKg`, `Notes` | Lifecycle hooks. A value worked out on load. A PDF certificate. |
| `MischiefReport` | `Culprit`, `Description`, `Severity`, `RecordedAt` | Soft delete. Full-text search. A date the server stamps. An audit trail. |
| `GiftShopVisit` | `SessionId`, `OccurredUtc`, `Path`, `Kind` | A high-volume activity table. Streaming. Change notifications. Marketing attribution. |

### Enum values

- `Mood`: `Content`, `Smug`, `Plotting`, `Hangry`, `Asleep`
- `OrderStatus`: `Placed`, `Ripening`, `Delivered`, `Eaten`
- `Severity`: `Cheeky`, `Brazen`, `Felonious`
- `VisitKind`: `Browse`, `Purchase`, `Refund`

### Stock rows

Reach for these before inventing a value.

- Troops: "The Loungers" (motto "Why stand"), "Second Breakfast Club", "The Escape Committee".
- Banana varieties: Cavendish, Plantain, Lady Finger, Red Dacca.
- An order: 400 Cavendish, of which 40 were substituted with Plantain, reason "supplier ran out".
- A mischief report: "Stole the keeper's lunch. Again.", `Severity.Brazen`, reported by Mavis.
- A weigh-in note: "Gerald: no change".
- A nap: 95 minutes, location "the good hammock".

## Beyond the database

The same sanctuary covers the other TFM libraries, so their examples need no new setting.

| Need | Sample |
|---|---|
| A web page (CMS) | "Meet the troop", "Visiting hours", "Adopt a monkey" |
| An email template | "Your banana order has shipped", "Weigh-in reminder" |
| A PDF template | Weigh-in certificate, adoption certificate |
| A stored file or image | A monkey's portrait, `gerald-portrait.jpg` |
| A signed-in user | A keeper |
| A payment | A banana order, or a gift shop purchase |
| An analytics or attribution event | A `GiftShopVisit` |
| An application name | `PortlyPrimate.Web`, `PortlyPrimate.Admin` |

## Rules

- Keep the jokes in the data, not in the explanation. The prose around an example stays plain.
- A sample still has to be correct: it compiles, and the names mean what the feature needs.
- Add a new entity or character here before using it, so the next author finds it.
