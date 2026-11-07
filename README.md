<p align="center"><img src="docs/logo.png" width="140"></p>
<h1 align="center">Rio Uninstaller</h1>
<p align="center">مزيل برامج سريع وبسيط لويندوز — واجهة سوداء حديثة، عربي وإنجليزي</p>

## التحميل
من صفحة **Releases** حمّل `RioUninstaller-Setup.exe` وثبّته.

## وش يسوي
اختر البرنامج من القائمة، وبعدها ضغطة وحدة:

| الزر | وش يسوي |
|---|---|
| **حذف عادي** (الافتراضي) | يقفل البرنامج، يشغّل مزيله بصمت، ويحذف ملفاته واختصاراته وخدماته ومهامه وتشغيله التلقائي ومفاتيح التسجيل الخاصة بالتثبيت — **ويبقي إعداداتك وبياناتك** (AppData / ProgramData / إعدادات الريجستري) عشان لو رجعت نزلته يرجع مثل ما كان |
| **حذف كامل** | نفس اللي فوق + يمسح كل بيانات وإعدادات البرنامج: مجلدات AppData و LocalLow و ProgramData و Temp ومفاتيح `HKCU/HKLM\Software\<البرنامج>` — كأنك ما نزلته أبد. يغني عن الفحص المتقدم وتحديد العناصر العريضة يدوياً |
| **إيقاف الخلفية** | يقفل كل عمليات البرنامج ويعطّل خدماته وتشغيله التلقائي ومهامه المجدولة **بدون حذف أي شيء** — وتقدر ترجعها بزر "إعادة تفعيل الخلفية" |

اختصارات: `Delete` = حذف عادي، `Shift+Delete` = حذف كامل.

## الأمان
- يحذف فقط الأشياء اللي اسمها يطابق البرنامج بالضبط أو اللي تشير لمجلد البرنامج نفسه.
- مجلدات النظام والمستخدم (Windows، Program Files، AppData، Documents…) ومجلدات الناشرين المشتركة ما تنحذف أبداً.
- أي مجلد فيه برنامج ثاني مثبّت ما ينلمس.
- مفاتيح الريجستري الحساسة (Microsoft، Classes، Policies، SYSTEM) محمية.

---

## English
Fast, simple uninstaller for Windows with a modern dark UI (Arabic / English).

- **Standard uninstall** (default): closes the app, runs its uninstaller silently, removes program files, shortcuts, services, tasks, startup entries — **keeps your settings & data**.
- **Complete wipe**: everything above plus all data/settings folders and registry keys — as if it was never installed.
- **Stop background**: kills its processes and disables its services, startup entries and scheduled tasks without deleting anything (reversible).

Built with .NET 8 / WPF. The installer is built by GitHub Actions on every push.
