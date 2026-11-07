using System.ComponentModel;

namespace RioUninstaller;

/// <summary>Tiny AR/EN string table. Bind in XAML with {Binding [Key], Source={x:Static local:Loc.I}}.</summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc I { get; } = new Loc();
    public event PropertyChangedEventHandler PropertyChanged;

    public bool IsArabic { get; private set; } = true;

    public void SetLanguage(bool arabic)
    {
        IsArabic = arabic;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsArabic)));
    }

    public string this[string key] =>
        Strings.TryGetValue(key, out var v) ? (IsArabic ? v.ar : v.en) : key;

    public static string T(string key) => I[key];

    static readonly Dictionary<string, (string ar, string en)> Strings = new()
    {
        ["AppTitle"] = ("Rio Uninstaller", "Rio Uninstaller"),
        ["Search"] = ("ابحث عن برنامج...", "Search programs..."),
        ["ColName"] = ("البرنامج", "Program"),
        ["Programs"] = ("البرامج", "Programs"),
        ["Count"] = ("{0} برنامج", "{0} programs"),
        ["SortName"] = ("الاسم", "Name"),
        ["SortSize"] = ("الحجم", "Size"),
        ["SortDate"] = ("الأحدث", "Newest"),
        ["Refresh"] = ("تحديث", "Refresh"),
        ["SelectHint"] = ("اختر برنامج من القائمة", "Select a program from the list"),
        ["SelectHint2"] = ("بعدها اختر طريقة الحذف بضغطة وحدة", "Then pick how to remove it — one click"),
        ["Publisher"] = ("الناشر", "Publisher"),
        ["Version"] = ("الإصدار", "Version"),
        ["Size"] = ("الحجم", "Size"),
        ["Installed"] = ("تاريخ التثبيت", "Installed"),
        ["Location"] = ("المسار", "Location"),
        ["Unknown"] = ("غير معروف", "Unknown"),
        ["Running"] = ("يعمل الآن", "Running"),
        ["Services"] = ("خدمات", "Services"),
        ["Tasks"] = ("مهام مجدولة", "Scheduled tasks"),
        ["Startup"] = ("تشغيل تلقائي", "Startup"),
        ["BgDisabledChip"] = ("الخلفية معطلة", "Background disabled"),

        ["NormalTitle"] = ("حذف عادي", "Standard uninstall"),
        ["NormalDesc"] = ("يحذف البرنامج ويبقي إعداداتك وبياناتك — لو نزلته مرة ثانية يرجع مثل ما كان",
                          "Removes the program but keeps your settings & data — reinstall and it's all back"),
        ["FullTitle"] = ("حذف كامل", "Complete wipe"),
        ["FullDesc"] = ("يمسح البرنامج وكل أثر له: الملفات، الإعدادات، الريجستري، الخدمات — كأنك ما نزلته أبد",
                        "Erases the program and every trace: files, settings, registry, services — as if never installed"),
        ["BgTitle"] = ("إيقاف الخلفية", "Stop background"),
        ["BgDesc"] = ("يقفل البرنامج ويعطّل خدماته وتشغيله التلقائي ومهامه بدون حذف أي شيء",
                      "Closes it and disables its services, startup and tasks without deleting anything"),
        ["BgRestoreTitle"] = ("إعادة تفعيل الخلفية", "Re-enable background"),
        ["BgRestoreDesc"] = ("يرجّع الخدمات والتشغيل التلقائي والمهام مثل ما كانت",
                             "Restores its services, startup entries and tasks as they were"),
        ["Recommended"] = ("الافتراضي", "Default"),

        ["ConfirmTitle"] = ("تأكيد", "Confirm"),
        ["ConfirmNormal"] = ("حذف \"{0}\"؟\nإعداداتك وبياناتك بتبقى محفوظة.", "Uninstall \"{0}\"?\nYour settings and data will be kept."),
        ["ConfirmFull"] = ("حذف \"{0}\" نهائياً مع كل بياناته وإعداداته؟\nهذا ما يمكن التراجع عنه.",
                           "Completely wipe \"{0}\" including all its data and settings?\nThis cannot be undone."),
        ["ConfirmBg"] = ("إيقاف وتعطيل كل ما يعمل في الخلفية لـ \"{0}\"؟", "Stop and disable everything \"{0}\" runs in the background?"),
        ["Yes"] = ("نعم، كمّل", "Yes, continue"),
        ["Cancel"] = ("إلغاء", "Cancel"),
        ["Done"] = ("تم", "Done"),
        ["Close"] = ("إغلاق", "Close"),
        ["ShowDetails"] = ("عرض التفاصيل", "Show details"),
        ["HideDetails"] = ("إخفاء التفاصيل", "Hide details"),

        ["Working"] = ("جاري العمل...", "Working..."),
        ["StepRestore"] = ("إنشاء نقطة استعادة", "Creating restore point"),
        ["StepKill"] = ("إغلاق البرنامج وعملياته", "Closing the program and its processes"),
        ["StepServices"] = ("إيقاف الخدمات", "Stopping services"),
        ["StepUninstaller"] = ("تشغيل مزيل البرنامج", "Running the program's uninstaller"),
        ["StepScan"] = ("فحص البقايا", "Scanning leftovers"),
        ["StepClean"] = ("حذف البقايا", "Removing leftovers"),
        ["StepDisable"] = ("تعطيل الخدمات والتشغيل التلقائي والمهام", "Disabling services, startup and tasks"),
        ["StepEnable"] = ("إعادة التفعيل", "Re-enabling"),

        ["ResultUninstalled"] = ("تم حذف {0}", "{0} was removed"),
        ["ResultWiped"] = ("تم مسح {0} بالكامل", "{0} was completely wiped"),
        ["ResultBg"] = ("تم إيقاف {0} في الخلفية", "{0} background activity stopped"),
        ["ResultBgRestore"] = ("تمت إعادة تفعيل {0}", "{0} background re-enabled"),
        ["ResultStats"] = ("{0} ملف ومجلد • {1} عنصر ريجستري • {2} خدمة • {3} مهمة/تشغيل تلقائي",
                           "{0} files & folders • {1} registry items • {2} services • {3} tasks/startup"),
        ["ResultBgStats"] = ("{0} عملية أُغلقت • {1} خدمة • {2} تشغيل تلقائي • {3} مهمة",
                             "{0} processes closed • {1} services • {2} startup entries • {3} tasks"),
        ["ResultKeptData"] = ("بياناتك وإعداداتك محفوظة", "Your data and settings were kept"),
        ["UninstallerCancelled"] = ("مزيل البرنامج انلغى أو فشل — ما تم حذف شيء", "The program's uninstaller was cancelled or failed — nothing was removed"),
        ["ForceRemove"] = ("حذف إجباري", "Force remove"),
        ["ForceAsk"] = ("مزيل البرنامج ما اشتغل أو انلغى.\nتبي Rio يحذفه إجبارياً؟", "The program's uninstaller didn't complete.\nForce remove it with Rio?"),
        ["Reboot"] = ("بعض الملفات مقفولة وبتنحذف بعد إعادة التشغيل", "Some locked files will be removed after restart"),

        ["Settings"] = ("الإعدادات", "Settings"),
        ["SetRestore"] = ("إنشاء نقطة استعادة قبل الحذف (أبطأ)", "Create a restore point before uninstalling (slower)"),
        ["SetSilent"] = ("حذف صامت تلقائي إذا البرنامج يدعمه", "Silent uninstall when the program supports it"),
        ["SetLanguage"] = ("اللغة", "Language"),
        ["Arabic"] = ("العربية", "Arabic"),
        ["English"] = ("English", "English"),
        ["Hidden"] = ("إظهار مكونات النظام", "Show system components"),
        ["OpenFolder"] = ("فتح المجلد", "Open folder"),
        ["UpdateAvailable"] = ("توفر تحديث جديد للبرنامج — اضغط للتحديث", "New update available — click to update"),
        ["Updating"] = ("جاري التحديث... {0}%", "Updating... {0}%"),
        ["UpdateFailed"] = ("فشل التحديث — اضغط للمحاولة مرة ثانية", "Update failed — click to retry"),
        ["NoResults"] = ("ما فيه نتائج", "No results"),
    };
}
