// Shared KUDU code: this file comes from kudu-common (https://github.com/azeembukhariprivate/kudu-common).
// Change it there and run its sync script; the build fails when this copy differs from kudu-common.
using System;
using System.Collections.Generic;

namespace Kudusync.Core
{
    /// <summary>
    /// The words every KUDU app uses (buttons, the shared windows), in each language: English, Malayalam, Hindi,
    /// Spanish, French, Arabic. An app's own language files can say any of them differently. The credit "Made by Azeem,
    /// with Claude and ♥" stays as it is in every language.
    /// </summary>
    public static class LangCommon
    {
        private static readonly string[] Codes = { "en", "ml", "hi", "es", "fr", "ar" };

        private static readonly string[][] Rows =
        {
            new[] { "OK", "ശരി", "ठीक है", "Aceptar", "OK", "موافق" },
            new[] { "Cancel", "റദ്ദാക്കുക", "रद्द करें", "Cancelar", "Annuler", "إلغاء" },
            new[] { "Save", "സേവ് ചെയ്യുക", "सहेजें", "Guardar", "Enregistrer", "حفظ" },
            new[] { "Close", "അടയ്ക്കുക", "बंद करें", "Cerrar", "Fermer", "إغلاق" },
            new[] { "Yes", "അതെ", "हाँ", "Sí", "Oui", "نعم" },
            new[] { "No", "അല്ല", "नहीं", "No", "Non", "لا" },
            new[] { "Open", "തുറക്കുക", "खोलें", "Abrir", "Ouvrir", "فتح" },
            new[] { "Open…", "തുറക്കുക…", "खोलें…", "Abrir…", "Ouvrir…", "فتح…" },
            new[] { "Choose…", "തിരഞ്ഞെടുക്കുക…", "चुनें…", "Elegir…", "Choisir…", "اختيار…" },
            new[] { "Browse…", "തിരയുക…", "ब्राउज़ करें…", "Examinar…", "Parcourir…", "استعراض…" },
            new[] { "Stop", "നിർത്തുക", "रोकें", "Detener", "Arrêter", "إيقاف" },
            new[] { "Start", "തുടങ്ങുക", "शुरू करें", "Iniciar", "Démarrer", "بدء" },
            new[] { "Back", "പിന്നോട്ട്", "वापस", "Atrás", "Retour", "رجوع" },
            new[] { "Next", "അടുത്തത്", "आगे", "Siguiente", "Suivant", "التالي" },
            new[] { "Done", "കഴിഞ്ഞു", "हो गया", "Listo", "Terminé", "تم" },
            new[] { "Delete", "ഇല്ലാതാക്കുക", "हटाएँ", "Eliminar", "Supprimer", "حذف" },
            new[] { "Remove", "നീക്കം ചെയ്യുക", "निकालें", "Quitar", "Retirer", "إزالة" },
            new[] { "Rename", "പേരുമാറ്റുക", "नाम बदलें", "Cambiar nombre", "Renommer", "إعادة تسمية" },
            new[] { "Copy", "പകർത്തുക", "कॉपी करें", "Copiar", "Copier", "نسخ" },
            new[] { "Paste", "ഒട്ടിക്കുക", "चिपकाएँ", "Pegar", "Coller", "لصق" },
            new[] { "Cut", "മുറിക്കുക", "काटें", "Cortar", "Couper", "قص" },
            new[] { "Undo", "പഴയപടിയാക്കുക", "पूर्ववत करें", "Deshacer", "Annuler la frappe", "تراجع" },
            new[] { "Redo", "വീണ്ടും ചെയ്യുക", "फिर से करें", "Rehacer", "Rétablir", "إعادة" },
            new[] { "Select all", "എല്ലാം തിരഞ്ഞെടുക്കുക", "सभी चुनें", "Seleccionar todo", "Tout sélectionner", "تحديد الكل" },
            new[] { "Search", "തിരയുക", "खोजें", "Buscar", "Rechercher", "بحث" },
            new[] { "Find", "കണ്ടെത്തുക", "ढूँढें", "Buscar", "Rechercher", "بحث" },
            new[] { "Refresh", "പുതുക്കുക", "ताज़ा करें", "Actualizar", "Actualiser", "تحديث" },
            new[] { "Export", "എക്സ്പോർട്ട് ചെയ്യുക", "निर्यात करें", "Exportar", "Exporter", "تصدير" },
            new[] { "Import", "ഇംപോർട്ട് ചെയ്യുക", "आयात करें", "Importar", "Importer", "استيراد" },
            new[] { "Print", "പ്രിന്റ് ചെയ്യുക", "प्रिंट करें", "Imprimir", "Imprimer", "طباعة" },
            new[] { "Help", "സഹായം", "सहायता", "Ayuda", "Aide", "مساعدة" },
            new[] { "Settings", "ക്രമീകരണങ്ങൾ", "सेटिंग्स", "Ajustes", "Paramètres", "الإعدادات" },
            new[] { "About", "ഈ ആപ്പിനെക്കുറിച്ച്", "इस ऐप के बारे में", "Acerca de", "À propos", "حول" },
            new[] { "Language", "ഭാഷ", "भाषा", "Idioma", "Langue", "اللغة" },
            new[] { "The same as Windows", "വിൻഡോസിലേതു പോലെ", "Windows जैसी ही", "El mismo que Windows", "La même que Windows", "نفس لغة Windows" },
            new[] { "The language changes the next time you start {0}.", "അടുത്ത തവണ {0} തുറക്കുമ്പോൾ ഭാഷ മാറും.", "अगली बार {0} शुरू करने पर भाषा बदल जाएगी।", "El idioma cambiará la próxima vez que abras {0}.", "La langue changera au prochain démarrage de {0}.", "ستتغير اللغة عند تشغيل {0} في المرة القادمة." },
            new[] { "Appearance", "രൂപം", "रूप", "Apariencia", "Apparence", "المظهر" },
            new[] { "Theme", "തീം", "थीम", "Tema", "Thème", "السمة" },
            new[] { "Light", "ഇളം നിറം", "हल्का", "Claro", "Clair", "فاتح" },
            new[] { "Dark", "ഇരുണ്ട നിറം", "गहरा", "Oscuro", "Sombre", "داكن" },
            new[] { "System", "സിസ്റ്റത്തിലേതു പോലെ", "सिस्टम जैसा", "Sistema", "Système", "النظام" },
            new[] { "Updates", "അപ്ഡേറ്റുകൾ", "अपडेट", "Actualizaciones", "Mises à jour", "التحديثات" },
            new[] { "Check for updates", "അപ്ഡേറ്റുകൾ പരിശോധിക്കുക", "अपडेट जाँचें", "Buscar actualizaciones", "Rechercher des mises à jour", "البحث عن تحديثات" },
            new[] { "Version {0}", "പതിപ്പ് {0}", "संस्करण {0}", "Versión {0}", "Version {0}", "الإصدار {0}" },
            new[] { "Licence", "ലൈസൻസ്", "लाइसेंस", "Licencia", "Licence", "الترخيص" },
            new[] { "Website", "വെബ്സൈറ്റ്", "वेबसाइट", "Sitio web", "Site web", "الموقع" },
            new[] { "Name", "പേര്", "नाम", "Nombre", "Nom", "الاسم" },
            new[] { "Size", "വലുപ്പം", "आकार", "Tamaño", "Taille", "الحجم" },
            new[] { "Date", "തീയതി", "तारीख", "Fecha", "Date", "التاريخ" },
            new[] { "Type", "തരം", "प्रकार", "Tipo", "Type", "النوع" },
            new[] { "Folder", "ഫോൾഡർ", "फ़ोल्डर", "Carpeta", "Dossier", "المجلد" },
            new[] { "File", "ഫയൽ", "फ़ाइल", "Archivo", "Fichier", "الملف" },
            new[] { "Files", "ഫയലുകൾ", "फ़ाइलें", "Archivos", "Fichiers", "الملفات" },
            new[] { "Folders", "ഫോൾഡറുകൾ", "फ़ोल्डर", "Carpetas", "Dossiers", "المجلدات" },
            new[] { "All files", "എല്ലാ ഫയലുകളും", "सभी फ़ाइलें", "Todos los archivos", "Tous les fichiers", "كل الملفات" },
            new[] { "Open folder", "ഫോൾഡർ തുറക്കുക", "फ़ोल्डर खोलें", "Abrir carpeta", "Ouvrir le dossier", "فتح المجلد" },
            new[] { "Show in folder", "ഫോൾഡറിൽ കാണിക്കുക", "फ़ोल्डर में दिखाएँ", "Mostrar en la carpeta", "Afficher dans le dossier", "إظهار في المجلد" },
            new[] { "{0} ran into a problem it couldn't get past, and has to close.", "{0}-ന് മറികടക്കാൻ കഴിയാത്ത ഒരു പ്രശ്നം ഉണ്ടായി; അതിനാൽ അടയ്ക്കേണ്ടിവരുന്നു.", "{0} में ऐसी समस्या आई जिससे वह आगे नहीं बढ़ सका, इसलिए इसे बंद होना पड़ेगा।", "{0} ha encontrado un problema que no ha podido superar y tiene que cerrarse.", "{0} a rencontré un problème qu'il n'a pas pu surmonter et doit se fermer.", "واجه {0} مشكلة لم يستطع تجاوزها، ولا بد من إغلاقه." }
        };

        /// <summary>The common words for a language, as English and its counterpart.</summary>
        public static IEnumerable<KeyValuePair<string, string>> For(string code)
        {
            var column = Array.IndexOf(Codes, code);
            if (column <= 0) yield break;
            foreach (var row in Rows)
                if (row.Length > column && row[column].Length > 0)
                    yield return new KeyValuePair<string, string>(row[0], row[column]);
        }
    }
}
