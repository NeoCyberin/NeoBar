using System;
using System.Collections.Generic;

// ======================================================================
//  Відповідність часових поясів IANA (Europe/Kyiv) -> Windows (FLE Standard Time)
//  Згенеровано з таблиці CLDR windowsZones (через пакет tzlocal).
// ======================================================================
static class TzMap
{
    const string Data =
        "AUS Central Standard Time=Australia/Darwin,Australia/North;AUS Eastern Standard Time=Australia/ACT,Australia/C" +
        "anberra,Australia/Melbourne,Australia/NSW,Australia/Sydney,Australia/Victoria;Afghanistan Standard Time=Asia/K" +
        "abul;Alaskan Standard Time=America/Anchorage,America/Juneau,America/Metlakatla,America/Nome,America/Sitka,Amer" +
        "ica/Yakutat,US/Alaska;Aleutian Standard Time=America/Adak,America/Atka,US/Aleutian;Altai Standard Time=Asia/Ba" +
        "rnaul;Arab Standard Time=Asia/Aden,Asia/Bahrain,Asia/Kuwait,Asia/Qatar,Asia/Riyadh;Arabian Standard Time=Asia/" +
        "Dubai,Asia/Muscat,Etc/GMT-4;Arabic Standard Time=Asia/Baghdad;Argentina Standard Time=America/Argentina/Buenos" +
        "_Aires,America/Argentina/Catamarca,America/Argentina/ComodRivadavia,America/Argentina/Cordoba,America/Argentin" +
        "a/Jujuy,America/Argentina/La_Rioja,America/Argentina/Mendoza,America/Argentina/Rio_Gallegos,America/Argentina/" +
        "Salta,America/Argentina/San_Juan,America/Argentina/San_Luis,America/Argentina/Tucuman,America/Argentina/Ushuai" +
        "a,America/Buenos_Aires,America/Catamarca,America/Cordoba,America/Jujuy,America/Mendoza,America/Rosario;Astrakh" +
        "an Standard Time=Europe/Astrakhan,Europe/Ulyanovsk;Atlantic Standard Time=America/Glace_Bay,America/Goose_Bay," +
        "America/Halifax,America/Moncton,America/Thule,Atlantic/Bermuda,Canada/Atlantic;Aus Central W. Standard Time=Au" +
        "stralia/Eucla;Azerbaijan Standard Time=Asia/Baku;Azores Standard Time=America/Scoresbysund,Atlantic/Azores;Bah" +
        "ia Standard Time=America/Bahia;Bangladesh Standard Time=Asia/Dacca,Asia/Dhaka,Asia/Thimbu,Asia/Thimphu;Belarus" +
        " Standard Time=Europe/Minsk;Bougainville Standard Time=Pacific/Bougainville;Canada Central Standard Time=Ameri" +
        "ca/Regina,America/Swift_Current,Canada/Saskatchewan;Cape Verde Standard Time=Atlantic/Cape_Verde,Etc/GMT+1;Cau" +
        "casus Standard Time=Asia/Yerevan;Cen. Australia Standard Time=Australia/Adelaide,Australia/Broken_Hill,Austral" +
        "ia/South,Australia/Yancowinna;Central America Standard Time=America/Belize,America/Costa_Rica,America/El_Salva" +
        "dor,America/Guatemala,America/Managua,America/Tegucigalpa,Etc/GMT+6,Pacific/Galapagos;Central Asia Standard Ti" +
        "me=Antarctica/Vostok,Asia/Almaty,Asia/Bishkek,Asia/Kashgar,Asia/Qostanay,Asia/Urumqi,Etc/GMT-6,Indian/Chagos;C" +
        "entral Brazilian Standard Time=America/Campo_Grande,America/Cuiaba;Central Europe Standard Time=Europe/Belgrad" +
        "e,Europe/Bratislava,Europe/Budapest,Europe/Ljubljana,Europe/Podgorica,Europe/Prague,Europe/Tirane;Central Euro" +
        "pean Standard Time=Europe/Sarajevo,Europe/Skopje,Europe/Warsaw,Europe/Zagreb,Poland;Central Pacific Standard T" +
        "ime=Antarctica/Casey,Etc/GMT-11,Pacific/Efate,Pacific/Guadalcanal,Pacific/Kosrae,Pacific/Noumea,Pacific/Pohnpe" +
        "i,Pacific/Ponape;Central Standard Time=America/Chicago,America/Indiana/Knox,America/Indiana/Tell_City,America/" +
        "Knox_IN,America/Matamoros,America/Menominee,America/North_Dakota/Beulah,America/North_Dakota/Center,America/No" +
        "rth_Dakota/New_Salem,America/Ojinaga,America/Rainy_River,America/Rankin_Inlet,America/Resolute,America/Winnipe" +
        "g,CST6CDT,Canada/Central,US/Central,US/Indiana-Starke;Central Standard Time (Mexico)=,America/Bahia_Banderas,A" +
        "merica/Chihuahua,America/Merida,America/Mexico_City,America/Monterrey,Mexico/General;Chatham Islands Standard " +
        "Time=NZ-CHAT,Pacific/Chatham;China Standard Time=Asia/Chongqing,Asia/Chungking,Asia/Harbin,Asia/Hong_Kong,Asia" +
        "/Macao,Asia/Macau,Asia/Shanghai,Hongkong,PRC;Cuba Standard Time=America/Havana,Cuba;Dateline Standard Time=Etc" +
        "/GMT+12;E. Africa Standard Time=Africa/Addis_Ababa,Africa/Asmara,Africa/Asmera,Africa/Dar_es_Salaam,Africa/Dji" +
        "bouti,Africa/Kampala,Africa/Mogadishu,Africa/Nairobi,Antarctica/Syowa,Etc/GMT-3,Indian/Antananarivo,Indian/Com" +
        "oro,Indian/Mayotte;E. Australia Standard Time=Australia/Brisbane,Australia/Lindeman,Australia/Queensland;E. Eu" +
        "rope Standard Time=Europe/Chisinau,Europe/Tiraspol;E. South America Standard Time=America/Sao_Paulo,Brazil/Eas" +
        "t;Easter Island Standard Time=Chile/EasterIsland,Pacific/Easter;Eastern Standard Time=America/Detroit,America/" +
        "Indiana/Petersburg,America/Indiana/Vincennes,America/Indiana/Winamac,America/Iqaluit,America/Kentucky/Louisvil" +
        "le,America/Kentucky/Monticello,America/Louisville,America/Montreal,America/Nassau,America/New_York,America/Nip" +
        "igon,America/Pangnirtung,America/Thunder_Bay,America/Toronto,Canada/Eastern,EST5EDT,US/Eastern,US/Michigan;Eas" +
        "tern Standard Time (Mexico)=America/Cancun;Egypt Standard Time=Africa/Cairo,Egypt;Ekaterinburg Standard Time=A" +
        "sia/Yekaterinburg;FLE Standard Time=Europe/Helsinki,Europe/Kiev,Europe/Kyiv,Europe/Mariehamn,Europe/Riga,Europ" +
        "e/Sofia,Europe/Tallinn,Europe/Uzhgorod,Europe/Vilnius,Europe/Zaporozhye;Fiji Standard Time=Pacific/Fiji;GMT St" +
        "andard Time=Atlantic/Canary,Atlantic/Faeroe,Atlantic/Faroe,Atlantic/Madeira,Eire,Europe/Belfast,Europe/Dublin," +
        "Europe/Guernsey,Europe/Isle_of_Man,Europe/Jersey,Europe/Lisbon,Europe/London,GB,GB-Eire,Portugal;GTB Standard " +
        "Time=Asia/Famagusta,Asia/Nicosia,Europe/Athens,Europe/Bucharest,Europe/Nicosia;Georgian Standard Time=Asia/Tbi" +
        "lisi;Greenland Standard Time=America/Godthab,America/Nuuk;Greenwich Standard Time=Africa/Abidjan,Africa/Accra," +
        "Africa/Bamako,Africa/Banjul,Africa/Bissau,Africa/Conakry,Africa/Dakar,Africa/Freetown,Africa/Lome,Africa/Monro" +
        "via,Africa/Nouakchott,Africa/Ouagadougou,Africa/Timbuktu,America/Danmarkshavn,Atlantic/Reykjavik,Atlantic/St_H" +
        "elena,Iceland;Haiti Standard Time=America/Port-au-Prince;Hawaiian Standard Time=Etc/GMT+10,Pacific/Honolulu,Pa" +
        "cific/Johnston,Pacific/Rarotonga,Pacific/Tahiti,US/Hawaii;India Standard Time=Asia/Calcutta,Asia/Kolkata;Iran " +
        "Standard Time=Asia/Tehran,Iran;Israel Standard Time=Asia/Jerusalem,Asia/Tel_Aviv,Israel;Jordan Standard Time=A" +
        "sia/Amman;Kaliningrad Standard Time=Europe/Kaliningrad;Korea Standard Time=Asia/Seoul,ROK;Libya Standard Time=" +
        "Africa/Tripoli,Libya;Line Islands Standard Time=Etc/GMT-14,Pacific/Kiritimati;Lord Howe Standard Time=Australi" +
        "a/LHI,Australia/Lord_Howe;Magadan Standard Time=Asia/Magadan;Magallanes Standard Time=America/Punta_Arenas;Mar" +
        "quesas Standard Time=Pacific/Marquesas;Mauritius Standard Time=Indian/Mahe,Indian/Mauritius,Indian/Reunion;Mid" +
        "dle East Standard Time=Asia/Beirut;Montevideo Standard Time=America/Montevideo;Morocco Standard Time=Africa/Ca" +
        "sablanca,Africa/El_Aaiun;Mountain Standard Time=America/Boise,America/Cambridge_Bay,America/Ciudad_Juarez,Amer" +
        "ica/Denver,America/Edmonton,America/Inuvik,America/Shiprock,America/Yellowknife,Canada/Mountain,MST7MDT,Navajo" +
        ",US/Mountain;Mountain Standard Time (Mexico)=America/Mazatlan,Mexico/BajaSur;Myanmar Standard Time=Asia/Rangoo" +
        "n,Asia/Yangon,Indian/Cocos;N. Central Asia Standard Time=Asia/Novosibirsk;Namibia Standard Time=Africa/Windhoe" +
        "k;Nepal Standard Time=Asia/Kathmandu,Asia/Katmandu;New Zealand Standard Time=Antarctica/McMurdo,Antarctica/Sou" +
        "th_Pole,NZ,Pacific/Auckland;Newfoundland Standard Time=America/St_Johns,Canada/Newfoundland;Norfolk Standard T" +
        "ime=Pacific/Norfolk;North Asia East Standard Time=Asia/Irkutsk;North Asia Standard Time=Asia/Krasnoyarsk,Asia/" +
        "Novokuznetsk;North Korea Standard Time=Asia/Pyongyang;Omsk Standard Time=Asia/Omsk;Pacific SA Standard Time=Am" +
        "erica/Santiago,Chile/Continental;Pacific Standard Time=America/Los_Angeles,America/Vancouver,Canada/Pacific,PS" +
        "T8PDT,US/Pacific;Pacific Standard Time (Mexico)=America/Ensenada,America/Santa_Isabel,America/Tijuana,Mexico/B" +
        "ajaNorte;Pakistan Standard Time=Asia/Karachi;Paraguay Standard Time=America/Asuncion;Qyzylorda Standard Time=A" +
        "sia/Qyzylorda;Romance Standard Time=Africa/Ceuta,Europe/Brussels,Europe/Copenhagen,Europe/Madrid,Europe/Paris;" +
        "Russia Time Zone 10=Asia/Srednekolymsk;Russia Time Zone 11=Asia/Anadyr,Asia/Kamchatka;Russia Time Zone 3=Europ" +
        "e/Samara;Russian Standard Time=Europe/Kirov,Europe/Moscow,Europe/Simferopol,W-SU;SA Eastern Standard Time=Amer" +
        "ica/Belem,America/Cayenne,America/Fortaleza,America/Maceio,America/Paramaribo,America/Recife,America/Santarem," +
        "Antarctica/Palmer,Antarctica/Rothera,Atlantic/Stanley,Etc/GMT+3;SA Pacific Standard Time=America/Atikokan,Amer" +
        "ica/Bogota,America/Cayman,America/Coral_Harbour,America/Eirunepe,America/Guayaquil,America/Jamaica,America/Lim" +
        "a,America/Panama,America/Porto_Acre,America/Rio_Branco,Brazil/Acre,Etc/GMT+5,Jamaica;SA Western Standard Time=" +
        "America/Anguilla,America/Antigua,America/Aruba,America/Barbados,America/Blanc-Sablon,America/Boa_Vista,America" +
        "/Curacao,America/Dominica,America/Grenada,America/Guadeloupe,America/Guyana,America/Kralendijk,America/La_Paz," +
        "America/Lower_Princes,America/Manaus,America/Marigot,America/Martinique,America/Montserrat,America/Port_of_Spa" +
        "in,America/Porto_Velho,America/Puerto_Rico,America/Santo_Domingo,America/St_Barthelemy,America/St_Kitts,Americ" +
        "a/St_Lucia,America/St_Thomas,America/St_Vincent,America/Tortola,America/Virgin,Brazil/West,Etc/GMT+4;SE Asia S" +
        "tandard Time=Antarctica/Davis,Asia/Bangkok,Asia/Ho_Chi_Minh,Asia/Jakarta,Asia/Phnom_Penh,Asia/Pontianak,Asia/S" +
        "aigon,Asia/Vientiane,Etc/GMT-7,Indian/Christmas;Saint Pierre Standard Time=America/Miquelon;Sakhalin Standard " +
        "Time=Asia/Sakhalin;Samoa Standard Time=Pacific/Apia;Sao Tome Standard Time=Africa/Sao_Tome;Saratov Standard Ti" +
        "me=Europe/Saratov;Singapore Standard Time=Asia/Brunei,Asia/Kuala_Lumpur,Asia/Kuching,Asia/Makassar,Asia/Manila" +
        ",Asia/Singapore,Asia/Ujung_Pandang,Etc/GMT-8,Singapore;South Africa Standard Time=Africa/Blantyre,Africa/Bujum" +
        "bura,Africa/Gaborone,Africa/Harare,Africa/Johannesburg,Africa/Kigali,Africa/Lubumbashi,Africa/Lusaka,Africa/Ma" +
        "puto,Africa/Maseru,Africa/Mbabane,Etc/GMT-2;South Sudan Standard Time=Africa/Juba;Sri Lanka Standard Time=Asia" +
        "/Colombo;Sudan Standard Time=Africa/Khartoum;Syria Standard Time=Asia/Damascus;Taipei Standard Time=Asia/Taipe" +
        "i,ROC;Tasmania Standard Time=Antarctica/Macquarie,Australia/Currie,Australia/Hobart,Australia/Tasmania;Tocanti" +
        "ns Standard Time=America/Araguaina;Tokyo Standard Time=Asia/Dili,Asia/Jayapura,Asia/Tokyo,Etc/GMT-9,Japan,Paci" +
        "fic/Palau;Tomsk Standard Time=Asia/Tomsk;Tonga Standard Time=Pacific/Tongatapu;Transbaikal Standard Time=Asia/" +
        "Chita;Turkey Standard Time=Asia/Istanbul,Europe/Istanbul,Turkey;Turks And Caicos Standard Time=America/Grand_T" +
        "urk;US Eastern Standard Time=America/Fort_Wayne,America/Indiana/Indianapolis,America/Indiana/Marengo,America/I" +
        "ndiana/Vevay,America/Indianapolis;US Mountain Standard Time=America/Creston,America/Dawson_Creek,America/Fort_" +
        "Nelson,America/Hermosillo,America/Phoenix,Etc/GMT+7,US/Arizona;UTC=Etc/GMT,Etc/GMT+0,Etc/GMT-0,Etc/GMT0,Etc/Gr" +
        "eenwich,Etc/UCT,Etc/UTC,Etc/Universal,Etc/Zulu,GMT+0,GMT-0,GMT0,Greenwich,UCT,UTC,Universal,Zulu;UTC+12=Etc/GM" +
        "T-12,Kwajalein,Pacific/Funafuti,Pacific/Kwajalein,Pacific/Majuro,Pacific/Nauru,Pacific/Tarawa,Pacific/Wake,Pac" +
        "ific/Wallis;UTC+13=Etc/GMT-13,Pacific/Enderbury,Pacific/Fakaofo,Pacific/Kanton;UTC-02=America/Noronha,Atlantic" +
        "/South_Georgia,Brazil/DeNoronha,Etc/GMT+2;UTC-08=Etc/GMT+8,Pacific/Pitcairn;UTC-09=Etc/GMT+9,Pacific/Gambier;U" +
        "TC-11=Etc/GMT+11,Pacific/Midway,Pacific/Niue,Pacific/Pago_Pago,Pacific/Samoa,US/Samoa;Ulaanbaatar Standard Tim" +
        "e=Asia/Choibalsan,Asia/Ulaanbaatar,Asia/Ulan_Bator;Venezuela Standard Time=America/Caracas;Vladivostok Standar" +
        "d Time=Asia/Ust-Nera,Asia/Vladivostok;Volgograd Standard Time=Europe/Volgograd;W. Australia Standard Time=Aust" +
        "ralia/Perth,Australia/West;W. Central Africa Standard Time=Africa/Algiers,Africa/Bangui,Africa/Brazzaville,Afr" +
        "ica/Douala,Africa/Kinshasa,Africa/Lagos,Africa/Libreville,Africa/Luanda,Africa/Malabo,Africa/Ndjamena,Africa/N" +
        "iamey,Africa/Porto-Novo,Africa/Tunis,Etc/GMT-1;W. Europe Standard Time=Arctic/Longyearbyen,Atlantic/Jan_Mayen," +
        "Europe/Amsterdam,Europe/Andorra,Europe/Berlin,Europe/Busingen,Europe/Gibraltar,Europe/Luxembourg,Europe/Malta," +
        "Europe/Monaco,Europe/Oslo,Europe/Rome,Europe/San_Marino,Europe/Stockholm,Europe/Vaduz,Europe/Vatican,Europe/Vi" +
        "enna,Europe/Zurich;W. Mongolia Standard Time=Asia/Hovd;West Asia Standard Time=Antarctica/Mawson,Asia/Aqtau,As" +
        "ia/Aqtobe,Asia/Ashgabat,Asia/Ashkhabad,Asia/Atyrau,Asia/Dushanbe,Asia/Oral,Asia/Samarkand,Asia/Tashkent,Etc/GM" +
        "T-5,Indian/Kerguelen,Indian/Maldives;West Bank Standard Time=Asia/Gaza,Asia/Hebron;West Pacific Standard Time=" +
        "Antarctica/DumontDUrville,Etc/GMT-10,Pacific/Chuuk,Pacific/Guam,Pacific/Port_Moresby,Pacific/Saipan,Pacific/Tr" +
        "uk,Pacific/Yap;Yakutsk Standard Time=Asia/Khandyga,Asia/Yakutsk;Yukon Standard Time=America/Dawson,America/Whi" +
        "tehorse,Canada/Yukon" +
        "";

    static Dictionary<string, string> map;

    public static string ToWindows(string iana)
    {
        if (string.IsNullOrEmpty(iana)) return null;
        if (map == null)
        {
            Dictionary<string, string> m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string grp in Data.Split(';'))
            {
                int eq = grp.IndexOf('=');
                if (eq <= 0) continue;
                string win = grp.Substring(0, eq);
                foreach (string z in grp.Substring(eq + 1).Split(',')) if (z.Length > 0) m[z] = win;
            }
            map = m;
        }
        string w;
        return map.TryGetValue(iana, out w) ? w : null;
    }
}
