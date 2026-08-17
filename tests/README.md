# Testovi — Uslužionica

## Pokretanje

```bash
# Svi testovi
dotnet test UsluzionicaServer.sln

# Samo unit (brzo, bez Dockera)
dotnet test tests/UsluzionicaServer.UnitTests/UsluzionicaServer.UnitTests.csproj

# Samo integracioni (ZAHTEVA POKRENUT DOCKER)
dotnet test tests/UsluzionicaServer.IntegrationTests/UsluzionicaServer.IntegrationTests.csproj

# Jedan test
dotnet test --filter "FullyQualifiedName~ReferralReward"
```

> **Integracioni testovi zahtevaju pokrenut Docker Desktop.** Bez njega padaju
> pri pokušaju startovanja SQL Server kontejnera.

---

## Struktura i zašto je baš takva

```
tests/
├── UsluzionicaServer.UnitTests/          čiste funkcije, bez I/O, ~100 ms ukupno
└── UsluzionicaServer.IntegrationTests/
    ├── Infrastructure/                   fixture, factory, builderi
    ├── Services/                         servis pozvan direktno, prava baza
    └── Api/                              pravi HTTP kroz WebApplicationFactory
```

### Zašto poslovna pravila NISU u unit testovima

Klasična piramida kaže „puno unit testova, malo integracionih". Kod ovog
projekta to ne radi, i vredi razumeti zašto:

```csharp
public sealed class BookingService(
    AppDbContext db, IConfiguration config,
    NotificationService notificationService, ILogger<BookingService> logger)
```

Servisi su `sealed`, bez interfejsa, i primaju `AppDbContext` direktno.
Posledice:

- **NSubstitute ih ne može zameniti** — `sealed` klasa se ne može naslediti.
- **Mock-ovanje `DbContext`-a je loša praksa** — mockovao bi LINQ provider, ne
  bazu. Test bi prolazio nad `List<T>` u memoriji, a produkcija bi pucala na
  pravom SQL upitu.

Zato pravila kao „referral nagrada se isplaćuje na aktivaciju provajdera" žive
u `IntegrationTests/Services/`. To nije kompromis nego tačna procena gde ta
pravila zapravo postoje.

U `UnitTests` ostaje ono što jeste čista jedinica: `MediaUrls`,
`SecretsGuard`, `TokenService`, `MessageEncryption`, `SearchNormalizer`.

---

## Alati i zašto baš oni

| Alat | Uloga | Zašto ne alternativa |
|---|---|---|
| **xUnit** | test runner | Standard u .NET ekosistemu; `IAsyncLifetime` se lepo uklapa sa async pripremom |
| **FluentAssertions** | tvrdnje | `.Should().Be(...)` daje poruku greške koja kaže *šta* je očekivano i *šta* je dobijeno; `Assert.Equal` kaže samo da se ne poklapaju. Verzija 7.x — od 8.x licenca je komercijalna |
| **Testcontainers** | pravi SQL Server u Dockeru | EF InMemory **nije baza** nego rečnik: ne poštuje unique indekse, ne podržava `ExecuteDeleteAsync`, ne izvršava SQL. Pretraga zavisi od `LIKE` i collation-a — InMemory bi davao lažno zeleno |
| **Respawn** | čišćenje baze između testova | Brže od rušenja baze; transakcija-po-testu ne radi jer servisi zovu `SaveChangesAsync` više puta, deo kroz `UserManager` |
| **WebApplicationFactory** | host u memoriji | Pokreće *pravi* `Program.cs` — isti DI, isti middleware, iste migracije. Menjaju se samo spoljne granice (baza, email) |
| **NSubstitute** | zamena za interfejse | Koristi se samo tamo gde interfejs postoji (`IEmailService`, `IHostEnvironment`) |

---

## Tri zamke koje moraš razumeti

### 1. Identity map — najčešći uzrok lažno zelenog testa

`DbContext` kešira entitete koje je učitao. Ako proveravaš rezultat kroz **isti**
`DbContext` koji je napravio izmenu, dobijaš instancu iz memorije — ne iz baze.

```csharp
// POGREŠNO
await bookingService.ConfirmAsync(id, providerId);
var b = await db.BookingRequests.FindAsync(id);   // može biti iz keša
b.Status.Should().Be(BookingStatus.Confirmed);    // prolazi i kad DB nije upisan

// ISPRAVNO — helper Query() otvara nov scope
var b = await Query(db => db.BookingRequests.SingleAsync(x => x.Id == id));
```

Zato `IntegrationTestBase.Query(...)` uvek otvara **nov scope**.

### 2. Env varijable nadjačavaju sve

`Program.cs` posle učitavanja `appsettings.Local.json` **ponovo** poziva
`AddEnvironmentVariables()`. To znači da `ConfigureAppConfiguration` u
`WebApplicationFactory` **ne može** da nadjača env varijablu.

Zato `DatabaseFixture` postavlja `ConnectionStrings__DefaultConnection` kao env
varijablu, pre nego što se host uopšte napravi.

### 3. Rate limiter obara sopstveni test suite

U `WebApplicationFactory` svi zahtevi dolaze sa iste (prazne) IP adrese, a
limiter particioniše po IP-u. Ceo suite deli **jednu** kvotu od 5 zahteva u
minuti.

Rešeno tako što su limiti preseljeni u konfiguraciju; test host ih postavlja na
100000, a klasa koja testira sam limiter ih spušta.

---

## Konvencija imenovanja

```
Metod_Uslov_OcekivanIshod
```

```csharp
[Fact] public async Task Registracija_SaReferralKodom_NeIsplacujeNagraduOdmah()
[Fact] public async Task AktivacijaProvajdera_KadaEmailNijePotvrdjen_Odbija()
```

Ime mora reći šta se štiti, bez otvaranja tela testa. Kad test padne u CI-ju,
često je ime jedino što vidiš.

## Pravilo: svaki test ima i negativan par

Test srećnog slučaja ne dokazuje da pravilo postoji — samo da kod ne puca.
Za svako pravilo mora postojati test koji dokazuje da **ne prolazi kad ne treba**.

Primer iz `ReferralRewardTests`:
- `AktivacijaProvajdera_KadaJeKorisnikBioPozvan_IsplacujeNagraduPozivaocu` — srećan slučaj
- `Registracija_SaReferralKodom_NeIsplacujeNagraduOdmah` — **ovaj hvata pravu grešku**

Drugi test bi pao ako bi neko „pojednostavio" registraciju tako što odmah
isplati nagradu — čime bi napravio rupu u kojoj svako može da otvara naloge sa
svojim kodom i uzima tokene bez rada.

---

## Provera da testovi zaista nešto štite

Pokvari jedno pravilo namerno i pokreni testove. Ako **tačno jedan** test padne
— i to onaj koji to pravilo štiti — testovi rade. Ako ne padne nijedan, test
postoji ali ne štiti ništa.

```bash
# npr. obriši EmailConfirmed proveru u ProviderService.ActivateAsync
dotnet test    # očekivano: pada AktivacijaProvajdera_KadaEmailNijePotvrdjen_Odbija
```
