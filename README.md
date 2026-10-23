# Revit Team Hub

**Near-live team work on one Revit project.** Built on Revit's own worksharing, so it's safe for the model: the add-in **syncs with central by itself** a few seconds after you stop editing, and a small server tells everyone else, whose Revit **reloads the latest changes by itself**. A teammate's edit shows up for you within seconds, without pressing anything.

- Auto Synchronize-with-Central after 5 s idle + auto Reload Latest when a teammate syncs
- One sync turn at a time (fewer failures over VPN), automatic central backups
- Version guard: stops a workshared model from being upgraded by a different Revit version by mistake
- Team pane inside Revit: who's online, their Revit version, what they're doing, chat
- One-click "create team project" and "join project"
- Revit **2025 and 2026** today · Arabic (Iraqi) interface

> 📘 الدليل الكامل بالعربي تحت 👇 · SketchUp version: [sketchup-team-hub](https://github.com/mhmdTaqi-code/sketchup-team-hub)

---

## شلون يشتغل

```
 Revit (محمد)  ─┐                         ┌─ الملف الرئيسي (Central) بفولدر مشترك على جهاز السيرفر
 Revit (صديق)  ─┼── Radmin VPN ──────────┤
 Revit (ثالث)  ─┘                         └─ Team Hub Server: دور المزامنة + الإشعارات + النسخ الاحتياطية
```

1. كل واحد يشتغل بنسخة **محلية** من الملف الرئيسي. هذا نظام Worksharing الأصلي بـ Revit.
2. تعدّل وتوكف **5 ثواني**، فالإضافة تسوي **Synchronize with Central** وحدها.
3. السيرفر يبلغ الباقين فوراً، وأجهزتهم تسوي **Reload Latest** وحدها.
4. تعديلك يظهر عندهم خلال ثواني، بدون ما أحد يدوس شي.

> **ليش مو لحظي 100% مثل Google Docs؟** لأن Revit نفسه ما يدعم هالشي، ولا أي برنامج تجاري. فبنينا على نظام Revit الأصلي حتى ما يتخرب الملف الرئيسي.

---

## اللي تحتاجه

| الشي | منو يحتاجه | منين |
|---|---|---|
| **Revit 2025 أو 2026** (نفس الإصدار لكل الفريق بالمشروع الواحد) | كل الفريق | Autodesk |
| **Node.js 22.13 أو أحدث** (LTS) | **جهاز السيرفر بس** | [nodejs.org](https://nodejs.org) |
| **Radmin VPN** | كل الفريق، إذا عن بعد | [radmin-vpn.com](https://www.radmin-vpn.com) |
| **الإضافة** `TeamHub-Revit.zip` | كل الفريق | صفحة [Releases](../../releases)، أو لوحة التحكم |
| .NET 8 SDK | بس إذا تريد تبني الإضافة من الكود | [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/8.0) |

### ⚠ الإصدارات: شغلة لازم تعرفها

- **Revit ما يكدر يحفظ بإصدار أقدم.** الملف اللي ينفتح بإصدار أحدث يترقى، وما يرجع.
- يعني **كل الفريق لازم يشتغل بنفس الإصدار بالمشروع الواحد.** تكدر تنصب أكثر من إصدار Revit على نفس الجهاز.
- الإضافة **توقفك** إذا حاولت تفتح مشروع فريق بإصدار غلط.
- إذا لازم تنزّل ملف لـ Revit 2023: زر **مساعد الإصدارات** يفتح [rvtconvert.com](https://rvtconvert.com). هذا موقع مستقل، مو من Autodesk، وملفك يترفع لسيرفرهم. تستخدمه **لتحويل مرة وحدة**، مو للشغل المباشر.

---

## الخطوة 1: جهاز السيرفر

### 1.1 نصّب Node.js
1. نزّل **LTS** من [nodejs.org](https://nodejs.org) (ملف `.msi`) ونصّبه بالإعدادات الافتراضية.
2. تأكد بـ Command Prompt:
   ```bash
   node -v
   ```

### 1.2 نزّل المشروع وشغّل السيرفر
1. **Code ← Download ZIP** وفك الضغط، أو:
   ```bash
   git clone https://github.com/mhmdTaqi-code/revit-team-hub.git
   ```
2. دبل كلك على **`start-server.bat`** وخلي نافذته مفتوحة. أول مرة يحتاج إنترنت.
3. ويندوز يسأل عن **Firewall** لـ Node.js: دوس **Allow access** على Private و Public.
4. افتح **http://localhost:8787** ← سوّي **حساب المدير** ← من تبويب **الفريق** أضف حساب لكل عضو.

### 1.3 Radmin VPN
1. نصّبه من [radmin-vpn.com](https://www.radmin-vpn.com) ← Network ← **Create Network** (اسم + كلمة سر).
2. عنوانك يبدي بـ **`26.`**. هذا اللي يستخدمه الفريق.

### 1.4 الفولدر المشترك للملف الرئيسي
شغّل **`revit\setup-revit-share.ps1`** كـ Administrator: كلك يمين ← **Run with PowerShell**، ووافق على صلاحيات المدير.

السكربت يسوي هذني الأشياء:
- يسوي فولدر `C:\TeamHub-Revit\Central`.
- يسوي حساب ويندوز اسمه **`teamhub`** بكلمة سر **انت تختارها**. الفريق يستخدمه حتى يوصل للفولدر.
- يشارك الفولدر باسم **`TeamHubRevit`**، وبس المدير و`teamhub` يوصلوله.
- يفتح مشاركة الملفات (بورت 445) **لشبكة Radmin بس** (`26.x.x.x`) وللشبكة المحلية.
- يفعّل النسخ الاحتياطي للملف الرئيسي بالسيرفر.

بعدها أعد تشغيل `start-server.bat`.

## الخطوة 2: كل عضو (ومنهم انت)

1. **Radmin VPN:** Network ← **Join Network** بنفس الاسم وكلمة السر.
2. **وصول للفولدر:** افتح File Explorer واكتب `\\26.x.x.x\TeamHubRevit`. اسم المستخدم `26.x.x.x\teamhub` وكلمة السر، وفعّل **Remember my credentials**.
3. **نصّب الإضافة:** نزّل `TeamHub-Revit.zip` من [Releases](../../releases) أو من `http://26.x.x.x:8787/download/teamhub_revit.zip`، فك الضغط، ودبل كلك على **`install.cmd`**. ما يحتاج صلاحيات مدير.
4. **افتح Revit.** إذا طلعت رسالة **"The publisher of this add-in could not be verified"**، اختار **Always Load**. هذي تطلع مرة وحدة بكل نسخة من الإضافة.
5. **اسمك بـ Revit:** File ← Options ← General ← **Username**. لازم **كل واحد يكون اسمه مختلف**، وإلا ملكية العناصر تتلخبط. اللوحة تنبهك إذا اثنين نفس الاسم.
6. تبويب **Team Hub** ← **الحساب**: عنوان السيرفر (`http://26.x.x.x:8787`) واسمك وكلمة سرك.

## الخطوة 3: بداية مشروع (المدير)

افتح الملف ← **Team Hub ← مشروع فريق جديد**. الإضافة تسوي كل شي:
- تشغّل Worksharing.
- تحفظ الملف الرئيسي بـ `\\26.x.x.x\TeamHubRevit\`.
- تسجله بالسيرفر.
- تفتحلك **نسخة محلية**. لا تشتغل بالملف الرئيسي مباشرة.

**الباقين:** Team Hub ← **انضمام لمشروع** ← يختارون المشروع ← الإضافة تسوي النسخة المحلية وتفتحها.

## الأزرار

| الزر | شنو يسوي |
|---|---|
| **لوحة الفريق** | منو متصل وبأي إصدار، شنو يسوي هسه، النشاط، الدردشة |
| **انضمام لمشروع** | يسوي نسخة محلية من الملف الرئيسي ويفتحها |
| **مزامنة هسه** | يزامن فوراً |
| **لايف تشغيل/إيقاف** | يشغّل أو يوقف المزامنة التلقائية |
| **مشروع فريق جديد** | للمدير بس |
| **الحساب** | دخول، خروج، لوحة التحكم |
| **مساعد الإصدارات** | إذا الفريق على إصدارات مختلفة |

## الحمايات

- **دور بالمزامنة:** واحد بس يزامن بكل مرة.
- **نسخ احتياطية:** نسخة من الملف الرئيسي بعد المزامنة (كحد أقصى كل 10 دقايق) بـ `server/data/revit-backups/`، وتبقى آخر 30 نسخة.
- **إذا السيرفر طفى:** الإضافة تزامن مباشرة ويا الملف الرئيسي، وتفحص كل 20 ثانية إذا أكو تعديلات.
- **الملكية:** بعد كل مزامنة ترجع العناصر اللي جنت تعدل عليها، حتى غيرك يكدر يعدلها فوراً. الـ Worksets اللي حجزتها بإيدك تبقى إلك.

## ⚠ تنبيه صريح عن العمل عن بعد

Autodesk تكول إن مشاركة الملف الرئيسي عبر VPN **مو مدعومة رسمياً**، وممكن تخرب الملف على اتصال بطيء أو متقطع.

- الحمايات اللي فوك تقلل الخطر.
- خلي الإنترنت مستقر، وخذ نسخ احتياطية.
- للمشاريع الكبيرة، الحل الرسمي هو **Autodesk Docs / BIM Collaborate Pro**، وهو مدفوع.

## حل المشاكل

| المشكلة | الحل |
|---|---|
| «ما أكدر أوصل للملف الرئيسي» | Radmin متصل؟ تكدر تفتح `\\26.x.x.x\TeamHubRevit` من File Explorer؟ |
| «ما كدرت أوصل للسيرفر» | `start-server.bat` شغال؟ البورت 8787 مسموح بالـ Firewall؟ |
| التبويب Team Hub ما طالع | شغّل `install.cmd` مرة ثانية، وتأكد إنك اخترت Always Load |
| «المشروع على Revit X» | افتح نفس إصدار المشروع |
| الملكية متلخبطة | كل واحد لازم يكون اسمه مختلف بـ Options ← Username |
| سجل الأخطاء | `%AppData%\TeamHub\revit.log` |

## حالة الاختبار

| الشي | الحالة |
|---|---|
| البناء، والتحميل داخل Revit 2025، وظهور التبويب | ✅ تم |
| تسجيل الدخول للسيرفر | ✅ تم |
| إنشاء الملف الرئيسي وتسجيل المشروع بالسيرفر | ✅ تم |
| الاختبار بجلستين (جدار من A يظهر عند B وحده وبالعكس) | ⏳ بعده |

**الاختبار بجلستين:** تكدر تشغله بنفسك بـ `TEAMHUB_SELFTEST`. شوف `SelfTest.cs`.

## البناء من الكود

يحتاج .NET 8 SDK، وRevit 2025 منصب. الإضافة تاخذ ملفات Revit من `C:\Program Files\Autodesk\Revit 2025`.

```bash
node tools/build-revit.js
```

هذا يطلّع `dist/TeamHub-Revit.zip`، وينشره على السيرفر.

## هيكل المشروع

```
revit/TeamHub.Revit/   الإضافة (C#)
  LiveSync.cs          محرك المزامنة واللايف
  App.cs               الشريط، الأحداث، منع فتح المشروع بإصدار غلط
  Commands.cs          الأزرار والنوافذ
  TeamPane.cs          لوحة الفريق
  Hub.cs               الاتصال بالسيرفر (HTTP + WebSocket)
revit/installer/       التنصيب، بدون صلاحيات مدير
revit/setup-revit-share.ps1   تجهيز الفولدر المشترك
server/                السيرفر ولوحة التحكم (نفسه يخدم SketchUp هم)
```

## الترخيص

MIT
