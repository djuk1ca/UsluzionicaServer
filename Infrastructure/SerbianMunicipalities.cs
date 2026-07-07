namespace UsluzionicaServer.Infrastructure;

/// <summary>
/// Statička lista svih opština u Srbiji.
/// Korisnik bira lokaciju iz ove liste — garantuje konzistentnost podataka u bazi.
/// </summary>
public static class SerbianMunicipalities
{
    public static readonly IReadOnlyList<string> All = new[]
    {
        // Beograd
        "Beograd — Barajevo", "Beograd — Čukarica", "Beograd — Grocka",
        "Beograd — Lazarevac", "Beograd — Mladenovac", "Beograd — Novi Beograd",
        "Beograd — Obrenovac", "Beograd — Palilula", "Beograd — Rakovica",
        "Beograd — Savski venac", "Beograd — Sopot", "Beograd — Stari grad",
        "Beograd — Surčin", "Beograd — Voždovac", "Beograd — Vračar",
        "Beograd — Zemun", "Beograd — Zvezdara",

        // Vojvodina — Severnobački okrug
        "Subotica", "Bačka Topola", "Mali Iđoš",

        // Vojvodina — Srednjebanatski okrug
        "Zrenjanin", "Nova Crnja", "Novi Bečej", "Žitište",

        // Vojvodina — Severnobanatski okrug
        "Kikinda", "Ada", "Kanjiža", "Novi Kneževac", "Senta", "Čoka",

        // Vojvodina — Južnobanatski okrug
        "Pančevo", "Alibunar", "Bela Crkva", "Kovačica", "Kovin", "Opovo", "Vršac",

        // Vojvodina — Zapadnobački okrug
        "Sombor", "Apatin", "Kula", "Odžaci",

        // Vojvodina — Južnobački okrug
        "Novi Sad", "Bač", "Bačka Palanka", "Bačka Karlovac", "Bečej",
        "Srbobran", "Temerin", "Titel", "Vrbas", "Žabalj",

        // Vojvodina — Sremski okrug
        "Sremska Mitrovica", "Inđija", "Irig", "Pećinci", "Ruma",
        "Stara Pazova", "Šid",

        // Šumadija i Zapadna Srbija — Šumadijski okrug
        "Kragujevac", "Aranđelovac", "Batočina", "Knić", "Lapovo",
        "Markovac", "Rača", "Topola",

        // Šumadija i Zapadna Srbija — Moravički okrug
        "Čačak", "Gornji Milanovac", "Ivanjica", "Lučani",

        // Šumadija i Zapadna Srbija — Rasinski okrug
        "Kruševac", "Aleksandrovac", "Brus", "Ćićevac", "Trstenik", "Varvarin",

        // Šumadija i Zapadna Srbija — Raški okrug
        "Novi Pazar", "Kraljevo", "Raška", "Tutin", "Vrnjačka Banja",

        // Šumadija i Zapadna Srbija — Kolubarski okrug
        "Valjevo", "Lajkovac", "Ljig", "Mionica", "Osečina", "Ub",

        // Šumadija i Zapadna Srbija — Mačvanski okrug
        "Šabac", "Bogatić", "Koceljeva", "Krupanj", "Ljubovija",
        "Loznica", "Mali Zvornik", "Vladimirci",

        // Šumadija i Zapadna Srbija — Zlatibarski okrug
        "Užice", "Arilje", "Bajina Bašta", "Čajetina", "Kosjerić",
        "Nova Varoš", "Priboj", "Prijepolje", "Požega", "Sjenica",

        // Šumadija i Zapadna Srbija — Pomoravski okrug
        "Jagodina", "Ćuprija", "Despotovac", "Paraćin", "Rekovac", "Svilajnac",

        // Južna i Istočna Srbija — Braničevski okrug
        "Požarevac", "Golubac", "Kučevo", "Malo Crniće", "Petrovac na Mlavi",
        "Velika Plana", "Žabari", "Žagubica",

        // Južna i Istočna Srbija — Borski okrug
        "Zaječar", "Bor", "Kladovo", "Negotin",

        // Južna i Istočna Srbija — Zaječarski okrug
        "Zaječar", "Boljevac", "Knjaževac", "Sokobanja",

        // Južna i Istočna Srbija — Nišavski okrug
        "Niš", "Aleksinac", "Doljevac", "Gadžin Han", "Merošina",
        "Ražanj", "Svrljig",

        // Južna i Istočna Srbija — Toplički okrug
        "Prokuplje", "Blace", "Kuršumlija", "Žitorađa",

        // Južna i Istočna Srbija — Pirotski okrug
        "Pirot", "Babušnica", "Bela Palanka", "Dimitrovgrad",

        // Južna i Istočna Srbija — Jablanički okrug
        "Leskovac", "Bojnik", "Crna Trava", "Lebane", "Medveđa", "Vlasotince",

        // Južna i Istočna Srbija — Pčinjski okrug
        "Vranje", "Bosilegrad", "Bujanovac", "Preševo", "Surdulica",
        "Trgovište", "Vladičin Han",

        // Južna i Istočna Srbija — Podunavski okrug
        "Smederevo", "Smederevska Palanka", "Velika Plana",

        // Južna i Istočna Srbija — Timočki okrug
        "Zaječar", "Bor", "Kladovo", "Negotin",
    }
    .Distinct()          // ukloni duplikate koji se pojavljuju u više okruga
    .OrderBy(x => x)     // abecedni red za lakšu pretragu
    .ToList()
    .AsReadOnly();
}
