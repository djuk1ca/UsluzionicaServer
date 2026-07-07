using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace UsluzionicaServer.Migrations
{
    /// <inheritdoc />
    public partial class AddExpandedCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "Categories",
                columns: new[] { "Id", "Name", "ParentId", "Slug", "SortOrder" },
                values: new object[,]
                {
                    { 9, "Zdravlje & Wellness", null, "zdravlje", 9 },
                    { 10, "Obrazovanje & Instrukcije", null, "obrazovanje", 10 },
                    { 11, "Kućni ljubimci", null, "kucni-ljubimci", 11 },
                    { 12, "Sport & Rekreacija", null, "sport", 12 },
                    { 13, "Hrana & Piće", null, "hrana", 13 },
                    { 14, "Frizerstvo — žene", 1, "frizerstvo-zene", 1 },
                    { 15, "Frizerstvo — muškarci", 1, "frizerstvo-muskarci", 2 },
                    { 16, "Frizerstvo — deca", 1, "frizerstvo-deca", 3 },
                    { 17, "Boja kose", 1, "boja-kose", 4 },
                    { 18, "Keratinski tretmani", 1, "keratinski-tretmani", 5 },
                    { 19, "Nadogradnja kose", 1, "nadogradnja-kose", 6 },
                    { 20, "Gel nokti", 1, "gel-nokti", 7 },
                    { 21, "Akrilni nokti", 1, "akrilni-nokti", 8 },
                    { 22, "Pedikir", 1, "pedikir", 9 },
                    { 23, "Manikir", 1, "manikir", 10 },
                    { 24, "Laminacija obrva", 1, "laminacija-obrva", 11 },
                    { 25, "Microblading", 1, "microblading", 12 },
                    { 26, "Nadogradnja trepavica", 1, "nadogradnja-trepavica", 13 },
                    { 27, "Lifting trepavica", 1, "lifting-trepavica", 14 },
                    { 28, "Svečana šminka", 1, "svecana-sminka", 15 },
                    { 29, "Svakodnevna šminka", 1, "svakodnevna-sminka", 16 },
                    { 30, "Trajni makeup — usne", 1, "trajni-makeup-usne", 17 },
                    { 31, "Trajni makeup — obrve", 1, "trajni-makeup-obrve", 18 },
                    { 32, "Depilacija vosak", 1, "depilacija-vosak", 19 },
                    { 33, "Depilacija šećerom", 1, "depilacija-secerom", 20 },
                    { 34, "Laser depilacija", 1, "laser-depilacija", 21 },
                    { 35, "Klasična masaža", 1, "klasicna-masaza", 22 },
                    { 36, "Relaks masaža", 1, "relaks-masaza", 23 },
                    { 37, "Sportska masaža", 1, "sportska-masaza", 24 },
                    { 38, "Maderoterapija", 1, "maderoterapija", 25 },
                    { 39, "Anticelulit masaža", 1, "anticelulit-masaza", 26 },
                    { 40, "Facial tretman", 1, "facial-tretman", 27 },
                    { 41, "Hemijski piling", 1, "hemijski-piling", 28 },
                    { 42, "Mikrodermabrazija", 1, "mikrodermabrazija", 29 },
                    { 43, "Tattoo", 1, "tattoo", 30 },
                    { 44, "Piercing", 1, "piercing", 31 },
                    { 45, "Ručno rađen nakit", 2, "rucni-nakit", 1 },
                    { 46, "Keramika", 2, "keramika", 2 },
                    { 47, "Pletenje i heklanje", 2, "pletenje-heklanje", 3 },
                    { 48, "Umetničke slike", 2, "umetnicke-slike", 4 },
                    { 49, "Akvarelne slike", 2, "akvarelne-slike", 5 },
                    { 50, "Ilustracije i crtež", 2, "ilustracije", 6 },
                    { 51, "Personalizovani pokloni", 2, "personalizovani-pokloni", 7 },
                    { 52, "Krojenje i šivenje", 2, "krojenje-sivenje", 8 },
                    { 53, "Šivenje po meri", 2, "sivenje-po-meri", 9 },
                    { 54, "Sveće", 2, "svece", 10 },
                    { 55, "Sapuni", 2, "sapuni", 11 },
                    { 56, "Makrame", 2, "makrame", 12 },
                    { 57, "Dekorativni predmeti", 2, "dekorativni-predmeti", 13 },
                    { 58, "Vez i tekstil", 2, "vez-tekstil", 14 },
                    { 59, "Foto venčanja", 3, "foto-vencanja", 1 },
                    { 60, "Foto krštenja i proslava", 3, "foto-krstenja", 2 },
                    { 61, "Foto portret", 3, "foto-portret", 3 },
                    { 62, "Foto novorođenčadi", 3, "foto-novorodjencadi", 4 },
                    { 63, "Foto produkt", 3, "foto-produkt", 5 },
                    { 64, "Foto nekretnina", 3, "foto-nekretnina", 6 },
                    { 65, "Foto eventi", 3, "foto-eventi", 7 },
                    { 66, "Video produkcija", 3, "video-produkcija", 8 },
                    { 67, "Montaža videa", 3, "montaza-videa", 9 },
                    { 68, "Drone snimanje", 3, "drone-snimanje", 10 },
                    { 69, "Reels i kratki video", 3, "reels-kratki-video", 11 },
                    { 70, "Modna fotografija", 3, "modna-fotografija", 12 },
                    { 71, "Vodoinstalater", 4, "vodoinstalater", 1 },
                    { 72, "Električar", 4, "elektricar", 2 },
                    { 73, "Moler", 4, "moler", 3 },
                    { 74, "Gletovanje", 4, "gletovanje", 4 },
                    { 75, "Stolar", 4, "stolar", 5 },
                    { 76, "Bravar", 4, "bravar", 6 },
                    { 77, "Keramičar", 4, "keramicar", 7 },
                    { 78, "Fasader", 4, "fasader", 8 },
                    { 79, "Zidar", 4, "zidar", 9 },
                    { 80, "Servis klima uređaja", 4, "servis-klima", 10 },
                    { 81, "Montaža klima uređaja", 4, "montaza-klima", 11 },
                    { 82, "Parketar", 4, "parketar", 12 },
                    { 83, "Krovopokrivač", 4, "krovopokrivac", 13 },
                    { 84, "Izolacija", 4, "izolacija", 14 },
                    { 85, "Renoviranje kupatila", 4, "renoviranje-kupatila", 15 },
                    { 86, "Renoviranje kuhinje", 4, "renoviranje-kuhinje", 16 },
                    { 87, "Montaža nameštaja", 4, "montaza-namestaja", 17 },
                    { 88, "Popravka kućnih aparata", 4, "popravka-aparata", 18 },
                    { 89, "Čišćenje dimnjaka", 4, "ciscenje-dimnjaka", 19 },
                    { 90, "Deratizacija i dezinsekcija", 4, "deratizacija", 20 },
                    { 91, "Uređenje venčanja", 5, "uredjenje-vencanja", 1 },
                    { 92, "Uređenje krštenja", 5, "uredjenje-krstenja", 2 },
                    { 93, "Uređenje proslava", 5, "uredjenje-proslava", 3 },
                    { 94, "Cvetni aranžmani", 5, "cvetni-aranzmani", 4 },
                    { 95, "Balonske dekoracije", 5, "balonske-dekoracije", 5 },
                    { 96, "Svetlosne instalacije", 5, "svetlosne-instalacije", 6 },
                    { 97, "DJ usluge", 5, "dj-usluge", 7 },
                    { 98, "Live muzika — bend", 5, "live-muzika", 8 },
                    { 99, "Pevač / Pevačica", 5, "pevac-pevacica", 9 },
                    { 100, "Fotoboks", 5, "fotoboks", 10 },
                    { 101, "Animatori za decu", 5, "animatori-decu", 11 },
                    { 102, "Iznajmljivanje stolova i stolica", 5, "iznajmljivanje-stolova", 12 },
                    { 103, "Catering", 5, "catering", 13 },
                    { 104, "Torte i kolači po porudžbini", 5, "torte-kolaci", 14 },
                    { 105, "Web dizajn", 6, "web-dizajn", 1 },
                    { 106, "Web razvoj", 6, "web-razvoj", 2 },
                    { 107, "Mobilne aplikacije", 6, "mobilne-aplikacije", 3 },
                    { 108, "SEO optimizacija", 6, "seo", 4 },
                    { 109, "Social media menadžment", 6, "social-media", 5 },
                    { 110, "Grafički dizajn", 6, "graficki-dizajn", 6 },
                    { 111, "Logo dizajn", 6, "logo-dizajn", 7 },
                    { 112, "Copywriting i tekstovi", 6, "copywriting", 8 },
                    { 113, "Email marketing", 6, "email-marketing", 9 },
                    { 114, "Google Ads", 6, "google-ads", 10 },
                    { 115, "Facebook & Instagram Ads", 6, "fb-ig-ads", 11 },
                    { 116, "Video animacije", 6, "video-animacije", 12 },
                    { 117, "UI/UX dizajn", 6, "ui-ux-dizajn", 13 },
                    { 118, "Brend identitet", 6, "brend-identitet", 14 },
                    { 119, "Fotografija za društvene mreže", 6, "foto-drustvene-mreze", 15 },
                    { 120, "Auto mehaničar", 7, "auto-mehanicar", 1 },
                    { 121, "Auto dijagnostika", 7, "auto-dijagnostika", 2 },
                    { 122, "Autopraonica", 7, "autopraonica", 3 },
                    { 123, "Auto detailing", 7, "auto-detailing", 4 },
                    { 124, "Auto limar", 7, "auto-limar", 5 },
                    { 125, "Poliranje i zaštita laka", 7, "poliranje-lak", 6 },
                    { 126, "Tapaciranje", 7, "tapaciranje", 7 },
                    { 127, "Auto električar", 7, "auto-elektricar", 8 },
                    { 128, "Gume i felne", 7, "gume-felne", 9 },
                    { 129, "Vuča vozila", 7, "vuca-vozila", 10 },
                    { 130, "Prevoz i transfer", 7, "prevoz-transfer", 11 },
                    { 131, "Rent-a-car", 7, "rent-a-car", 12 },
                    { 132, "Prevod tekstova", 8, "prevod-tekstova", 1 },
                    { 133, "Simultano prevođenje", 8, "simultano-prevodjenje", 2 },
                    { 134, "Čišćenje stanova", 8, "ciscenje-stanova", 3 },
                    { 135, "Čišćenje poslovnih prostora", 8, "ciscenje-poslovnih", 4 },
                    { 136, "Čišćenje tepiha i nameštaja", 8, "ciscenje-tepisa", 5 },
                    { 137, "Selidbe", 8, "selidbe", 6 },
                    { 138, "Briga o deci (dadilja)", 8, "dadilja", 7 },
                    { 139, "Vrtlarstvo i uređenje dvorišta", 8, "vrtlarstvo", 8 },
                    { 140, "IT podrška i servis računara", 8, "it-podrska", 9 },
                    { 141, "Računovodstvo i knjigovodstvo", 8, "racunovodstvo", 10 },
                    { 142, "Pravne usluge i konsultacije", 8, "pravne-usluge", 11 },
                    { 143, "Organizacija putovanja", 8, "organizacija-putovanja", 12 },
                    { 144, "Fizioterapeut", 9, "fizioterapeut", 1 },
                    { 145, "Radna terapija", 9, "radna-terapija", 2 },
                    { 146, "Psiholog i psihoterapeut", 9, "psiholog", 3 },
                    { 147, "Logoped", 9, "logoped", 4 },
                    { 148, "Nutricionista i dijetetičar", 9, "nutricionista", 5 },
                    { 149, "Akupunktura", 9, "akupunktura", 6 },
                    { 150, "Refleksologija", 9, "refleksologija", 7 },
                    { 151, "Reiki", 9, "reiki", 8 },
                    { 152, "Meditacija i mindfulness", 9, "meditacija", 9 },
                    { 153, "Lični coach", 9, "licni-coach", 10 },
                    { 154, "Hiropraktičar", 9, "hiroprakticar", 11 },
                    { 155, "Instrukcije matematika", 10, "instrukcije-matematika", 1 },
                    { 156, "Instrukcije fizika i hemija", 10, "instrukcije-fizika", 2 },
                    { 157, "Instrukcije srpski jezik", 10, "instrukcije-srpski", 3 },
                    { 158, "Instrukcije engleski jezik", 10, "instrukcije-engleski", 4 },
                    { 159, "Instrukcije nemački / francuski / španski", 10, "instrukcije-strani-jezici", 5 },
                    { 160, "Muzičke lekcije", 10, "muzicke-lekcije", 6 },
                    { 161, "Plesne lekcije", 10, "plesne-lekcije", 7 },
                    { 162, "Instrukcije programiranja", 10, "instrukcije-programiranja", 8 },
                    { 163, "Instrukcije umetnosti i crtanja", 10, "instrukcije-umetnosti", 9 },
                    { 164, "Priprema za prijemni / maturu", 10, "priprema-prijemni", 10 },
                    { 165, "Šišanje kućnih ljubimaca (grooming)", 11, "grooming", 1 },
                    { 166, "Dresura i obuka psa", 11, "dresura-psa", 2 },
                    { 167, "Petsitting (čuvanje ljubimaca)", 11, "petsitting", 3 },
                    { 168, "Šetanje pasa", 11, "setanje-pasa", 4 },
                    { 169, "Veterinarska kućna poseta", 11, "vet-kucna-poseta", 5 },
                    { 170, "Fotografija kućnih ljubimaca", 11, "foto-ljubimci", 6 },
                    { 171, "Izrada opreme za ljubimce", 11, "oprema-ljubimci", 7 },
                    { 172, "Lični fitnes trener", 12, "fitnes-trener", 1 },
                    { 173, "Joga instruktor", 12, "joga", 2 },
                    { 174, "Pilates instruktor", 12, "pilates", 3 },
                    { 175, "Plivanje — instruktor", 12, "plivanje-instruktor", 4 },
                    { 176, "Tenis — instruktor", 12, "tenis-instruktor", 5 },
                    { 177, "Fudbal — trener (deca)", 12, "fudbal-trener", 6 },
                    { 178, "Boks i borilačke veštine", 12, "boks-borilacke", 7 },
                    { 179, "Planinarenje — vodič", 12, "planinarenje-vodic", 8 },
                    { 180, "Biciklizam — organizovane ture", 12, "biciklizam-ture", 9 },
                    { 181, "Privatni kuvar / Chef", 13, "privatni-kuvar", 1 },
                    { 182, "Priprema obroka za nedelju", 13, "meal-prep", 2 },
                    { 183, "Dostava domaće hrane", 13, "dostava-domace-hrane", 3 },
                    { 184, "Kurs kuvanja", 13, "kurs-kuvanja", 4 },
                    { 185, "Barista i priprema kafe", 13, "barista", 5 },
                    { 186, "Izrada domaćih likera i rakija", 13, "domaci-likeri", 6 },
                    { 187, "Dekoracija torti", 13, "dekoracija-torti", 7 },
                    { 188, "Veganski i specijalni meni", 13, "veganski-meni", 8 }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 14);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 15);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 16);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 17);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 18);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 19);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 20);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 21);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 22);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 23);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 24);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 25);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 26);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 27);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 28);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 29);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 30);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 31);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 32);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 33);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 34);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 35);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 36);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 37);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 38);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 39);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 40);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 41);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 42);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 43);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 44);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 45);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 46);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 47);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 48);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 49);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 50);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 51);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 52);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 53);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 54);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 55);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 56);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 57);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 58);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 59);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 60);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 61);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 62);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 63);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 64);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 65);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 66);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 67);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 68);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 69);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 70);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 71);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 72);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 73);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 74);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 75);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 76);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 77);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 78);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 79);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 80);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 81);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 82);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 83);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 84);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 85);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 86);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 87);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 88);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 89);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 90);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 91);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 92);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 93);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 94);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 95);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 96);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 97);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 98);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 99);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 100);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 101);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 102);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 103);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 104);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 105);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 106);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 107);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 108);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 109);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 110);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 111);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 112);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 113);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 114);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 115);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 116);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 117);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 118);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 119);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 120);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 121);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 122);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 123);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 124);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 125);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 126);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 127);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 128);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 129);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 130);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 131);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 132);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 133);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 134);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 135);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 136);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 137);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 138);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 139);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 140);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 141);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 142);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 143);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 144);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 145);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 146);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 147);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 148);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 149);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 150);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 151);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 152);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 153);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 154);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 155);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 156);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 157);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 158);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 159);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 160);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 161);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 162);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 163);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 164);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 165);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 166);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 167);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 168);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 169);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 170);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 171);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 172);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 173);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 174);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 175);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 176);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 177);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 178);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 179);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 180);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 181);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 182);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 183);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 184);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 185);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 186);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 187);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 188);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 9);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 10);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 11);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 12);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 13);
        }
    }
}
