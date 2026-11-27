using System.Diagnostics;

sealed class InstructionsPage : UserControl
{
    private const string PairCommand = @".\scrcpy\adb.exe -P 5041 pair TV_IP:PAIR_PORT";
    private readonly ComboBox topics = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TableLayoutPanel guide = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
    private readonly RichTextBox reader = new()
    {
        ReadOnly = true, BorderStyle = BorderStyle.None, DetectUrls = true,
        WordWrap = true, ScrollBars = RichTextBoxScrollBars.Vertical, Dock = DockStyle.Fill,
        Font = new Font("Segoe UI", 11)
    };

    internal InstructionsPage()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(layout);
        layout.Controls.Add(new Label
        {
            Text = L("Connect your TV", "توصيل التلفزيون"), Dock = DockStyle.Fill,
            Font = new Font("Segoe UI Variable Text", 19, FontStyle.Bold)
        });
        topics.Dock = DockStyle.Fill;
        topics.Margin = new Padding(0, 4, 0, 10);
        topics.AccessibleName = L("Choose an instruction topic", "اختر موضوع الإرشادات");
        foreach (var section in Sections) topics.Items.Add(L(section.EnglishTitle, section.ArabicTitle));
        layout.Controls.Add(topics);
        var card = new FluentCard { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(16) };
        guide.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        guide.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        guide.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        guide.Controls.Add(reader, 0, 0);
        var commandPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        commandPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        commandPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        commandPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        commandPanel.Controls.Add(new Label { Text = L("Pairing command — replace TV_IP and PAIR_PORT", "أمر الإقران — استبدل TV_IP وPAIR_PORT"), Dock = DockStyle.Fill });
        commandPanel.Controls.Add(new TextBox
        {
            Text = PairCommand, ReadOnly = true, Dock = DockStyle.Fill,
            RightToLeft = RightToLeft.No, TextAlign = HorizontalAlignment.Left,
            Font = new Font("Consolas", 10),
            AccessibleName = L("Pairing command to copy", "أمر الإقران للنسخ")
        });
        guide.Controls.Add(commandPanel, 0, 1);
        card.Controls.Add(guide);
        layout.Controls.Add(card);
        reader.AccessibleName = L("TV connection instructions", "إرشادات توصيل التلفزيون");
        reader.RightToLeft = IsArabic ? RightToLeft.Yes : RightToLeft.No;
        reader.LinkClicked += (_, e) =>
        {
            // Only the two built-in documentation links can launch a browser.
            if (e.LinkText is not ("https://developer.android.com/tools/adb" or "https://developer.android.com/studio/debug/dev-options")) return;
            try { Process.Start(new ProcessStartInfo(e.LinkText) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            { MessageBox.Show(FindForm(), ex.Message, T("Instructions")); }
        };
        topics.SelectedIndexChanged += (_, _) => ShowTopic();
        reader.ForeColorChanged += (_, _) => ApplyReaderColor();
        topics.SelectedIndex = 0;
    }

    internal void FocusTopic() => topics.Focus();
    private static string L(string english, string arabic) => IsArabic ? arabic : english;

    private void ShowTopic()
    {
        if (topics.SelectedIndex < 0) return;
        var section = Sections[topics.SelectedIndex];
        var title = L(section.EnglishTitle, section.ArabicTitle);
        var pairing = topics.SelectedIndex == 5;
        guide.RowStyles[1].Height = pairing ? 64 * DeviceDpi / 96f : 0;
        guide.GetControlFromPosition(0, 1)!.Visible = pairing;
        var body = L(section.EnglishBody, section.ArabicBody).Replace(PairCommand,
            L("Use the pairing command below this guide.", "استخدم أمر الإقران أسفل هذا الدليل."));
        reader.Text = title + "\n\n" + body;
        ApplyReaderColor();
        reader.Select(0, 0);
        reader.ScrollToCaret();
    }

    private void ApplyReaderColor()
    {
        var start = reader.SelectionStart;
        var length = reader.SelectionLength;
        reader.SelectAll();
        reader.SelectionColor = reader.ForeColor;
        reader.Select(start, length);
    }

    private sealed record Section(string EnglishTitle, string ArabicTitle, string EnglishBody, string ArabicBody);

    private static readonly Section[] Sections =
    [
        new("Start here — connection checklist", "ابدأ هنا — خطوات التوصيل", """
        Keep the TV turned on and keep its physical remote nearby. First-time access needs approval on the TV.

        Android TV / Google TV
        1. Connect the PC and TV to the same home network. Ethernet, Wi-Fi, or one of each can work when the router lets the devices communicate.
        2. Unlock Developer options, then enable the debugging connection offered by your TV. See topics 2 and 3.
        3. Find the TV's current IP address. In the TVs tab, choose Scan for TVs, or Add TV if the scan misses it.
        4. Approve this PC on the TV, then choose Refresh. Select the TV to use Watch live, Remote, Audio or TV Tools.

        LG webOS
        Skip Android developer options. Follow the LG topic and use Pair LG in the TVs tab.

        Being discovered does not mean a TV has allowed control. Samsung Tizen and other non-Android TVs do not gain Android controls by selecting the Android TV type.

        Use the topic selector above to read the detailed steps. You can select and copy this text.
        """, """
        اترك التلفزيون قيد التشغيل واجعل جهاز التحكم الأصلي قريبًا منك. يلزم الموافقة على شاشة التلفزيون عند الاتصال لأول مرة.

        Android TV / Google TV
        1. صِل الكمبيوتر والتلفزيون بالشبكة المنزلية نفسها. يمكن استخدام Ethernet أو Wi-Fi أو أحدهما لكل جهاز إذا كان الموجّه يسمح بتواصل الأجهزة.
        2. أظهر خيارات المطوّر، ثم فعّل اتصال تصحيح الأخطاء الذي يدعمه التلفزيون. راجع الموضوعين 2 و3.
        3. اعرف عنوان IP الحالي للتلفزيون. من تبويب التلفزيونات اضغط «فحص الشبكة»، أو «إضافة تلفزيون» إذا لم يجده الفحص.
        4. وافق على السماح لهذا الكمبيوتر من شاشة التلفزيون، ثم اضغط «تحديث». اختر التلفزيون لاستخدام المشاهدة المباشرة أو التحكم أو الصوت أو أدوات التلفزيون.

        LG webOS
        لا تحتاج خيارات مطوّر Android. راجع موضوع LG واستخدم «إقران LG» في تبويب التلفزيونات.

        ظهور التلفزيون في الفحص لا يعني أنه سمح بالتحكم. أجهزة Samsung Tizen والأنظمة الأخرى لا تصبح Android بمجرد اختيار نوع Android TV.

        اختر موضوعًا من القائمة أعلاه لقراءة الخطوات التفصيلية. يمكنك تحديد النص ونسخه.
        """),
        new("1. Network, Ethernet, Wi-Fi & IP address", "1. الشبكة وEthernet وWi-Fi وعنوان IP", """
        Choose a connection
        • Ethernet: connect a LAN cable from the TV to your home router or switch, and check the TV says Connected.
        • Wi-Fi: connect the TV to your normal home Wi-Fi. Connect the PC to that home network too.
        • The PC can use Ethernet while the TV uses Wi-Fi. The router must bridge them on the same local network. The two devices do not have to use the same cable or Wi-Fi band.
        • Avoid guest Wi-Fi, hotel networks and router settings called AP isolation / client isolation. These can let both devices reach the internet while blocking them from each other. A VPN on the PC may also block local access.

        Find the right address
        On the TV, open Settings → Network & Internet / Network → the connected Wi-Fi or Ethernet network → Details / Status. Some TVs show it under About → Status. Write down the IPv4 address, for example 192.168.1.50.

        Use the address of the active TV connection. Ethernet and Wi-Fi can have different addresses; changing connections or restarting the router can change the IP. The PC's address, router address and TV MAC address are not the TV IP address.

        In TV Monitor, enter the IP only in the IPv4 field and the debugging port in the separate Port field. A reserved IP in your router can keep the address stable.

        A wired connection does not automatically enable debugging. Newer Wireless debugging may still require Wi-Fi to be enabled on the TV; follow the menu on your model.
        """, """
        اختر طريقة الاتصال
        • Ethernet: صِل كابل LAN من التلفزيون إلى الموجّه أو المبدّل المنزلي، وتأكد من ظهور حالة «متصل» على التلفزيون.
        • Wi-Fi: صِل التلفزيون بشبكة المنزل العادية، وصِل الكمبيوتر بالشبكة المنزلية نفسها.
        • يمكن أن يكون الكمبيوتر متصلًا بكابل والتلفزيون عبر Wi-Fi. يجب أن يربط الموجّه بينهما داخل الشبكة المحلية نفسها. لا يلزم استخدام الكابل نفسه أو نطاق Wi-Fi نفسه.
        • تجنّب شبكة الضيوف وشبكات الفنادق وإعدادات AP isolation / client isolation. قد تسمح بالإنترنت للجهازين لكنها تمنع تواصلهما. قد يمنع VPN على الكمبيوتر الوصول المحلي أيضًا.

        اعرف العنوان الصحيح
        على التلفزيون افتح الإعدادات ← الشبكة والإنترنت / الشبكة ← اتصال Wi-Fi أو Ethernet المستخدم ← التفاصيل / الحالة. قد يظهر العنوان في «حول ← الحالة». دوّن عنوان IPv4 مثل 192.168.1.50.

        استخدم عنوان اتصال التلفزيون النشط. قد يختلف عنوان Ethernet عن Wi-Fi، وقد يتغير IP بعد تغيير الاتصال أو إعادة تشغيل الموجّه. عنوان الكمبيوتر أو الموجّه أو عنوان MAC ليس عنوان IP للتلفزيون.

        في TV Monitor أدخل IP فقط في خانة IPv4، والمنفذ في خانة Port المنفصلة. يمكنك حجز IP للتلفزيون في إعدادات الموجّه لتثبيت عنوانه.

        توصيل الكابل لا يفعّل تصحيح الأخطاء تلقائيًا. قد يتطلب «التصحيح اللاسلكي» الحديث تشغيل Wi-Fi على التلفزيون؛ اتبع الخيارات المتاحة في طرازك.
        """),
        new("2. Show Developer options on Android TV", "2. إظهار خيارات المطوّر على Android TV", """
        Use the TV's physical remote:

        1. Open Settings.
        2. Try System → About (Google TV) or Device Preferences → About (many Android TVs). Menu names vary by brand.
        3. Highlight Android TV OS build / Build / Build number.
        4. Press OK seven times, until the developer confirmation appears. If the TV asks for an owner PIN, enter it on the TV.
        5. Go back to System / Device Preferences and open Developer options.

        Do not change unrelated developer settings. Next, read topic 3 to enable the debugging connection.

        Official Android guide:
        https://developer.android.com/studio/debug/dev-options
        """, """
        استخدم جهاز التحكم الأصلي للتلفزيون:

        1. افتح «الإعدادات».
        2. جرّب «النظام ← حول» في Google TV، أو «تفضيلات الجهاز ← حول» في كثير من أجهزة Android TV. تختلف أسماء القوائم حسب الشركة.
        3. حدّد «إصدار نظام Android TV / Android TV OS build» أو «رقم الإصدار / Build number».
        4. اضغط OK سبع مرات حتى تظهر رسالة تفعيل وضع المطوّر. إذا طُلِب رمز المالك فأدخله على التلفزيون نفسه.
        5. ارجع إلى «النظام / تفضيلات الجهاز» وافتح «خيارات المطوّر».

        لا تغيّر إعدادات المطوّر الأخرى. انتقل إلى الموضوع 3 لتفعيل اتصال تصحيح الأخطاء.

        دليل Android الرسمي:
        https://developer.android.com/studio/debug/dev-options
        """),
        new("3. Enable debugging & approve this PC", "3. تفعيل التصحيح والسماح لهذا الكمبيوتر", """
        In Developer options, enable USB debugging / ADB debugging. If available, enable Network debugging / Debugging over network too. A setting named USB debugging alone does not guarantee network access.

        Legacy network debugging often uses port 5555, unless the TV shows another port. It can work over the TV's Ethernet or Wi-Fi connection when the firmware supports it.

        If the TV offers Wireless debugging → Pair device with pairing code, read topic 5 instead. Do not use its pairing port as the connection port.

        Return to the TVs tab and connect. Watch the TV for “Allow USB debugging?” even when using a network connection. Approve only your PC; choose “Always allow from this computer” if you trust it. Then Refresh in TV Monitor.

        If no prompt appears, check the IP, network and debugging port. A scan cannot enable developer options or accept the TV's permission for you.

        Enable debugging only on a trusted local network. Do not forward its port from your router to the internet. You can turn debugging off on the TV when you no longer need control.
        """, """
        من «خيارات المطوّر» فعّل «تصحيح أخطاء USB / USB debugging» أو «تصحيح ADB». وإذا ظهر «تصحيح الشبكة / Network debugging» ففعّله أيضًا. تشغيل خيار USB وحده لا يضمن إتاحة الاتصال عبر الشبكة.

        يستخدم تصحيح الشبكة التقليدي غالبًا المنفذ 5555، إلا إذا عرض التلفزيون منفذًا آخر. يمكنه العمل عبر Ethernet أو Wi-Fi إذا كان نظام التلفزيون يدعمه.

        إذا ظهر «التصحيح اللاسلكي ← إقران الجهاز برمز»، فراجع الموضوع 5. لا تستخدم منفذ الإقران بدل منفذ الاتصال.

        ارجع إلى تبويب التلفزيونات وابدأ الاتصال. راقب رسالة «السماح بتصحيح أخطاء USB؟» على التلفزيون حتى عند الاتصال عبر الشبكة. وافق على كمبيوترك فقط، واختر «السماح دائمًا من هذا الكمبيوتر» إذا كنت تثق به، ثم اضغط «تحديث» في البرنامج.

        إذا لم يظهر الطلب، افحص IP والشبكة ومنفذ التصحيح. لا يستطيع فحص الشبكة إظهار خيارات المطوّر أو قبول الموافقة بدلًا منك.

        فعّل التصحيح على شبكة محلية موثوقة فقط. لا تفتح منفذه للإنترنت من الموجّه. يمكنك إيقاف التصحيح من التلفزيون عندما لا تحتاج التحكم.
        """),
        new("4. Add the TV & start controlling it", "4. إضافة التلفزيون وبدء التحكم", """
        1. Return to the TVs tab. Choose Scan for TVs. If your TV appears, select it and check its type and address using Edit TV.
        2. If it does not appear, choose Add TV. Enter a name, its current IPv4 address, Android TV / Google TV, and the debugging connection port. Use 5555 only for a TV using that legacy port. Wake MAC is optional.
        3. Save, approve the debugging prompt on the TV, then choose Refresh. Keep the screen awake during initial setup.
        4. Confirm the TV shows On and a current app/input. A device marked Unknown is not confirmed off.
        5. Select Watch live for video with audio by default. Use Remote for buttons, Audio for supported sound/microphone features, or TV Tools for setup and apps.

        While watching, click the floating remote icon at the top-right of the video. It follows the stream window and opens controls for that TV. It closes automatically when the stream ends.

        The mouse controls a pointer on the TV while the live view is open: hover and click tabs, icons and menus. While the video holds the mouse, press Alt (or the Windows key) to get it back on the PC.

        First use of some features installs a helper on Android TV. Screen/audio capture can ask for a separate on-TV approval; approve it on the TV when you start that feature. Protected content may not be captured.

        Power on over the network depends on the TV's standby networking and wake support. A fully disconnected TV cannot receive commands.
        """, """
        1. ارجع إلى تبويب «التلفزيونات» واضغط «فحص الشبكة». إذا ظهر جهازك فاختره وافحص النوع والعنوان عبر «تعديل التلفزيون».
        2. إذا لم يظهر فاضغط «إضافة تلفزيون». أدخل اسمًا وعنوان IPv4 الحالي ونوع Android TV / Google TV ومنفذ اتصال التصحيح. استخدم 5555 فقط إذا كان التلفزيون يستخدم هذا المنفذ التقليدي. عنوان Wake MAC اختياري.
        3. احفظ، ووافق على طلب التصحيح من التلفزيون، ثم اضغط «تحديث». اترك الشاشة قيد التشغيل أثناء الإعداد الأول.
        4. تأكد من ظهور «يعمل» والتطبيق أو المدخل الحالي. حالة «غير معروف» لا تؤكد أن التلفزيون مطفأ.
        5. اختر «مشاهدة مباشرة» لعرض الفيديو مع الصوت افتراضيًا. استخدم «التحكم» للأزرار أو «الصوت» لميزات الصوت والميكروفون المدعومة أو «أدوات التلفزيون» للإعداد والتطبيقات.

        أثناء المشاهدة اضغط أيقونة الريموت العائمة أعلى يمين الفيديو. تتبع نافذة البث وتفتح التحكم بالتلفزيون الذي تشاهده، وتُغلق تلقائيًا عند انتهاء البث.

        تتحكم الفأرة بمؤشر على التلفزيون أثناء المشاهدة المباشرة: مرّر وانقر على التبويبات والأيقونات والقوائم. عندما يحتجز الفيديو المؤشر اضغط Alt أو مفتاح Windows لإعادته إلى الكمبيوتر.

        تثبّت بعض الميزات برنامجًا مساعدًا عند أول استخدام على Android TV. قد يتطلب التقاط الشاشة أو الصوت موافقة منفصلة على التلفزيون؛ وافق عليها عند بدء الميزة. قد يتعذر التقاط المحتوى المحمي.

        يعتمد التشغيل عبر الشبكة على اتصال التلفزيون في وضع الاستعداد ودعمه للإيقاظ. لا يمكن إرسال أوامر إلى تلفزيون مفصول كليًا عن الشبكة.
        """),
        new("5. Advanced: Wireless debugging with a code", "5. متقدم: التصحيح اللاسلكي برمز إقران", """
        On supported TVs, Wireless debugging uses code-based pairing. TV Monitor has no pairing-code box. Pair first using its bundled ADB, then add the connection port in TV Monitor.

        1. On the TV, enable Wireless debugging. Open Pair device with pairing code. Keep its code and pairing address visible.
        2. On the PC, open the TV Monitor folder, right-click empty space and choose Open in Terminal (PowerShell).
        3. Replace TV_IP and PAIR_PORT below with the TV's pairing address and run:

        .\scrcpy\adb.exe -P 5041 pair TV_IP:PAIR_PORT

        Enter the TV's code when the command asks. Then close the TV's pairing-code screen and read IP address & port from the main Wireless debugging screen.

        4. Add / edit the TV in TV Monitor using that connection port, then Refresh. It can differ from the pairing port and change later.

        The PC-side 5041 above belongs to TV Monitor; do not put it into the TV Port field. Some TVs require Wi-Fi for this mode even with Ethernet connected.

        Official ADB guide:
        https://developer.android.com/tools/adb
        """, """
        تدعم بعض التلفزيونات «التصحيح اللاسلكي» برمز إقران. لا توجد خانة رمز إقران في TV Monitor. أقرِن أولًا بأداة ADB المرفقة، ثم أضف منفذ الاتصال في البرنامج.

        1. فعّل «التصحيح اللاسلكي» على التلفزيون، وافتح «إقران الجهاز برمز». اترك الرمز وعنوان الإقران ظاهرين.
        2. على الكمبيوتر افتح مجلد TV Monitor، واضغط بزر الفأرة الأيمن على مساحة فارغة واختر «فتح في Terminal» باستخدام PowerShell.
        3. استبدل TV_IP وPAIR_PORT بعنوان التلفزيون ومنفذ الإقران، ثم نفّذ:

        .\scrcpy\adb.exe -P 5041 pair TV_IP:PAIR_PORT

        أدخل رمز التلفزيون عندما يطلبه الأمر. بعدها أغلق شاشة الرمز واقرأ «عنوان IP والمنفذ» من الصفحة الرئيسية للتصحيح اللاسلكي.

        4. أضف التلفزيون أو عدّله في البرنامج مستخدمًا منفذ الاتصال هذا، ثم اضغط «تحديث». قد يختلف عن منفذ الإقران وقد يتغير لاحقًا.

        الرقم 5041 في الأمر هو منفذ خدمة البرنامج على الكمبيوتر؛ لا تضعه في خانة منفذ التلفزيون. قد يتطلب هذا الوضع تشغيل Wi-Fi حتى مع توصيل Ethernet.

        دليل ADB الرسمي:
        https://developer.android.com/tools/adb
        """),
        new("6. Connection problems & missing options", "6. مشاكل الاتصال والخيارات غير الموجودة", """
        TV found, controls unavailable
        Discovery only finds devices. Check that the type is correct, debugging is enabled and this PC has permission.

        Unknown / no response
        Turn on the TV. Recheck the current IP and port, particularly after switching Ethernet/Wi-Fi. Check for guest Wi-Fi, client isolation or a VPN that blocks local traffic. If Windows Firewall prompts for the app, allow it only on your trusted private network; do not disable the firewall.

        Unauthorized / approve debugging
        Look at the TV screen and approve this PC. If you previously denied it, switch TV debugging off and on, then Refresh. Some TVs have a Revoke USB debugging authorizations option; this also removes approvals for your other computers.

        No Network debugging, or port 5555 stays closed
        USB debugging alone may not expose a network port. Consult your TV manufacturer's instructions. Some models need an initial authorized USB debugging connection to enable ADB over TCP/IP; others do not expose a usable debug USB connection. Do not assume a normal USB-A media socket supports this. TV Monitor cannot enable an unreachable TV's debugging remotely.

        Wireless debugging stops after reboot
        Reopen its TV menu, confirm it is enabled, and update the connection port in Edit TV. Pair again if the TV no longer trusts the PC.

        Video works, audio does not
        Start with Watch live, whose audio is enabled by default. Read the app's status message and approve any TV capture prompt. Audio support depends on the Android version and the source app; protected playback may block capture.
        """, """
        ظهر التلفزيون لكن أدوات التحكم غير متاحة
        الفحص يكتشف الأجهزة فقط. تأكد من النوع الصحيح وتفعيل التصحيح وموافقة التلفزيون على هذا الكمبيوتر.

        غير معروف / لا توجد استجابة
        شغّل التلفزيون وأعد فحص IP والمنفذ، خاصة بعد التبديل بين Ethernet وWi-Fi. افحص شبكة الضيوف وعزل الأجهزة وVPN الذي قد يمنع الاتصال المحلي. إذا طلب جدار حماية Windows السماح للبرنامج فاسمح له على شبكتك الخاصة الموثوقة فقط؛ لا تعطّل جدار الحماية.

        غير مصرح / الموافقة على التصحيح
        انظر إلى شاشة التلفزيون ووافق على هذا الكمبيوتر. إذا رفضت سابقًا فأوقف التصحيح على التلفزيون وأعد تشغيله، ثم اضغط «تحديث». قد يتوفر خيار «إلغاء تصاريح تصحيح USB»؛ لكنه يلغي أيضًا موافقات أجهزة الكمبيوتر الأخرى.

        لا يوجد تصحيح شبكة أو يبقى 5555 مغلقًا
        قد لا يفتح خيار USB وحده منفذ شبكة. راجع تعليمات الشركة المصنّعة. تحتاج بعض الطرازات اتصال تصحيح USB أوليًا مصرحًا به لتفعيل ADB عبر TCP/IP، بينما لا تتيح طرازات أخرى اتصال USB مناسبًا للتصحيح. لا تفترض أن منفذ USB-A العادي للوسائط يدعم ذلك. لا يمكن للبرنامج تفعيل التصحيح عن بُعد على تلفزيون لا يستطيع الوصول إليه.

        توقف التصحيح اللاسلكي بعد إعادة التشغيل
        افتح قائمته على التلفزيون وتأكد من تفعيله، وحدّث منفذ الاتصال في «تعديل التلفزيون». أعد الإقران إذا لم يعد التلفزيون يثق بالكمبيوتر.

        الصورة تعمل دون صوت
        ابدأ بـ«مشاهدة مباشرة» التي تفعّل الصوت افتراضيًا. اقرأ رسالة الحالة ووافق على طلب التقاط الصوت على التلفزيون. يعتمد الصوت على إصدار Android والتطبيق المشغّل؛ قد يمنع المحتوى المحمي التقاطه.
        """),
        new("7. LG webOS & other TV systems", "7. أجهزة LG webOS والأنظمة الأخرى", """
        LG webOS uses a different connection method. Android Developer options and ADB are not required for this app's LG controls. You do not need to install LG's Developer Mode app for Pair LG.

        1. Turn on the LG TV and connect it to the same home network as the PC, by Ethernet or Wi-Fi.
        2. Choose Scan for TVs, or Add TV with type LG webOS, the current TV IP and port 3000 (the app's local WebSocket connection).
        3. Select the TV and choose Pair LG. Accept the TV Monitor prompt on the LG screen, then Refresh.
        4. If no prompt arrives, check the address, local-network access and your TV's settings for allowing mobile / local control. Menu names vary by model.

        Available controls depend on the TV. This app's Android live mirroring, APK installation and Android microphone helpers are not LG features.

        Samsung Tizen and other systems
        A scan may discover them, but this app does not provide their native control protocol. Leave their type as Other / unknown instead of pretending they are Android TVs. An attached Android TV box is a separate device; add that box's IP if you want to control it.
        """, """
        يستخدم LG webOS طريقة اتصال مختلفة. لا يحتاج تحكم LG في هذا البرنامج خيارات مطوّر Android أو ADB. ولا تحتاج تثبيت تطبيق Developer Mode من LG لاستخدام «إقران LG».

        1. شغّل تلفزيون LG وصِله بالشبكة المنزلية نفسها المتصل بها الكمبيوتر عبر Ethernet أو Wi-Fi.
        2. اضغط «فحص الشبكة»، أو «إضافة تلفزيون» واختر LG webOS وعنوان IP الحالي والمنفذ 3000، وهو اتصال WebSocket المحلي الذي يستخدمه البرنامج.
        3. اختر التلفزيون واضغط «إقران LG». وافق على طلب TV Monitor من شاشة LG ثم اضغط «تحديث».
        4. إذا لم يظهر الطلب فافحص العنوان والوصول المحلي وإعداد السماح بتحكم الهاتف أو التحكم المحلي على التلفزيون. تختلف أسماء القوائم حسب الطراز.

        تختلف الأدوات المتاحة حسب التلفزيون. المشاهدة المباشرة الخاصة بـAndroid وتثبيت APK ومساعدات ميكروفون Android ليست ميزات LG في هذا البرنامج.

        Samsung Tizen والأنظمة الأخرى
        قد تكتشفها الشبكة، لكن البرنامج لا يوفر بروتوكول التحكم الخاص بها. اترك نوعها «أخرى / غير معروف» بدل اختيار Android. جهاز Android TV الخارجي المتصل بالتلفزيون جهاز مستقل؛ أضف عنوان IP لذلك الجهاز للتحكم به.
        """),
        new("8. Install apps with Google Play", "8. تثبيت التطبيقات من Google Play", """
        1. Select a connected Android TV in the TVs tab. Google Play must be installed on that TV.
        2. If you want to see the TV on your PC, start Watch live first.
        3. Choose Google Play or TV Tools. Enter an app name to search, or paste its Google Play app link / package ID to open its listing.
        4. Choose Find on Google Play. Open Google Play opens the store for browsing instead.
        5. On the TV, choose Install. The TV remote button opens this program's remote so you can navigate with arrows and OK.
        6. Google account, purchase and permission prompts stay on the TV. Complete them there; do not enter account passwords into TV Monitor.
        7. To check the result, enter the app's link or package ID and choose Check installation. An app name alone cannot identify an installed package. Refresh TV apps to see its TV launcher when available.

        Google Play determines whether an app is available for that TV and account. The program opens the store; it does not silently install or purchase apps. Existing Home/screensaver restore history is preserved. Install APK remains a separate option for a local file you choose.
        """, """
        1. اختر تلفزيون Android متصلًا من تبويب التلفزيونات. يجب أن يكون Google Play مثبّتًا على التلفزيون.
        2. إذا أردت مشاهدة شاشة التلفزيون على الكمبيوتر فابدأ «مشاهدة مباشرة» أولًا.
        3. اختر Google Play أو «أدوات التلفزيون». أدخل اسم التطبيق للبحث، أو الصق رابط التطبيق في Google Play أو معرّف الحزمة لفتح صفحته.
        4. اضغط «بحث في Google Play». ويمكنك اختيار «فتح Google Play» لتصفّح المتجر مباشرة.
        5. اختر «تثبيت» على التلفزيون. يفتح زر «ريموت التلفزيون» جهاز التحكم في البرنامج لاستخدام الأسهم وOK.
        6. تظهر طلبات حساب Google والشراء والأذونات على التلفزيون؛ أكملها هناك، ولا تدخل كلمات مرور الحساب في TV Monitor.
        7. لفحص النتيجة أدخل رابط التطبيق أو معرّف الحزمة واضغط «فحص التثبيت». لا يكفي اسم التطبيق وحده لتحديد الحزمة المثبّتة. حدّث تطبيقات التلفزيون لرؤية زر تشغيله إذا كان متاحًا للتلفزيون.

        يحدّد Google Play توفر التطبيق لهذا التلفزيون والحساب. يفتح البرنامج المتجر ولا يثبّت أو يشتري التطبيقات بصمت. يحتفظ بسجل استعادة الرئيسية وشاشة التوقف السابق. يبقى «تثبيت APK» خيارًا منفصلًا لملف محلي تختاره.
        """)
        ,new("9. Send files, movies and audio to TV storage", "9. إرسال الملفات والأفلام والصوت إلى التلفزيون", """
        1. Connect and authorize an Android TV / Google TV using the earlier setup instructions. Use Wi-Fi or Ethernet on the same network.
        2. Select the TV, then choose Send files to TV on the dashboard or in TV Tools.
        3. Choose Add files. Select one or more movies, music tracks, documents or other files from your PC.
        4. Choose a destination such as /sdcard/Movies/TV Monitor, /sdcard/Music/TV Monitor or /sdcard/Download/TV Monitor. You can type another folder inside /sdcard/. The app creates it when needed.
        5. Choose Send to TV and watch the progress. Keep the TV connected. Large movies can take longer on Wi-Fi. Duplicate names receive a number; existing files are kept.
        6. Results show each saved TV path. Open that folder using a TV file manager or media player. Transfer does not automatically start playback or install an APK.
        7. Cancel transfer stops the current upload. Completed files remain. Cleanup of the unfinished temporary file is attempted. A disconnected TV may retain a hidden .tvmonitor-*.part file, which you can remove with a TV file manager.

        This uses writable Android shared storage, not private app or system folders. The app checks free space and permissions. Playback depends on the TV player's supported formats. LG webOS and other TV systems do not provide this Android storage-upload interface.
        """, """
        1. صِل تلفزيون Android TV أو Google TV ووافق على الكمبيوتر وفق إرشادات الإعداد السابقة. يمكن استخدام Wi-Fi أو Ethernet على الشبكة نفسها.
        2. اختر التلفزيون ثم «إرسال ملفات إلى التلفزيون» من الواجهة الرئيسية أو أدوات التلفزيون.
        3. اضغط «إضافة ملفات» واختر ملفًا أو أكثر من الأفلام أو الصوت أو المستندات على الكمبيوتر.
        4. اختر مجلدًا مثل /sdcard/Movies/TV Monitor للأفلام أو /sdcard/Music/TV Monitor للصوت أو /sdcard/Download/TV Monitor. يمكنك كتابة مجلد آخر داخل /sdcard/ وينشئه البرنامج عند الحاجة.
        5. اضغط «إرسال إلى التلفزيون» وتابع التقدم. اترك التلفزيون متصلًا. قد تستغرق الأفلام الكبيرة وقتًا أطول على Wi-Fi. تُرقّم الأسماء المكررة مع الحفاظ على الملفات الأصلية.
        6. تعرض النتائج مسار كل ملف محفوظ. افتح المجلد بمدير ملفات أو مشغل وسائط على التلفزيون. لا يبدأ النقل التشغيل تلقائيًا ولا يثبّت APK.
        7. يوقف «إلغاء النقل» الرفع الحالي مع إبقاء الملفات المكتملة. يحاول البرنامج حذف الملف المؤقت؛ إذا انقطع الاتصال فقد يبقى ملف مخفي باسم .tvmonitor-*.part يمكنك حذفه بمدير ملفات التلفزيون.

        تستخدم الميزة مساحة Android المشتركة القابلة للكتابة ولا ترفع إلى مجلدات النظام أو بيانات التطبيقات الخاصة. يفحص البرنامج المساحة والأذونات. يعتمد تشغيل الملف على صيغ مشغل التلفزيون. لا توفر أجهزة LG webOS والأنظمة الأخرى واجهة رفع تخزين Android هذه.
        """)
        ,new("10. Check available TV memory and storage", "10. فحص ذاكرة التلفزيون ومساحة التخزين", """
        Select a connected Android TV / Google TV and choose Memory & storage on the dashboard or in TV Tools. The file-transfer window also has a TV memory button.

        TV storage shows total, used and available shared storage (/sdcard/) for files. The usage bar shows used space. Android may reserve some space, so used plus available can be less than the total. A warning appears below 1 GiB available.

        TV RAM shows working memory, including how much Android estimates is available for applications. It is separate from file storage. RAM changes as apps run and is not space for saving movies. If a TV cannot report RAM, the app says so.

        Choose Refresh memory for a new reading. The timestamp identifies the latest successful check. The app does not delete files or clear memory. This feature requires authorized Android network debugging and is not available for LG webOS in this app.
        """, """
        اختر تلفزيون Android TV أو Google TV متصلًا ثم «الذاكرة والتخزين» من الواجهة الرئيسية أو أدوات التلفزيون. ويوجد زر «ذاكرة التلفزيون» في نافذة نقل الملفات.

        يعرض تخزين التلفزيون المساحة الإجمالية والمستخدمة والمتاحة داخل /sdcard/ للملفات، ويبيّن الشريط نسبة الاستخدام. قد يحجز Android جزءًا من المساحة، لذلك قد لا يساوي مجموع المستخدم والمتاح الإجمالي. يظهر تنبيه عندما تقل المساحة المتاحة عن 1 GiB.

        تعرض RAM ذاكرة التشغيل والمساحة التي يقدّر Android أنها متاحة للتطبيقات. وهي منفصلة عن تخزين الملفات وتتغير مع تشغيل التطبيقات، ولا تستخدم لحفظ الأفلام. إذا تعذر قراءتها يوضّح البرنامج ذلك.

        اضغط «تحديث الذاكرة» لقراءة جديدة. يوضح الوقت آخر فحص ناجح. لا يحذف البرنامج ملفات ولا يمسح الذاكرة. تتطلب الميزة تفويض التصحيح عبر الشبكة في Android، ولا تتوفر لأجهزة LG webOS في هذا البرنامج.
        """)
        ,new("11. IPv6 addresses, detection and power-on", "11. عناوين IPv6 والاكتشاف والتشغيل", """
        IP address and Wake MAC are different fields. An address starting fe80:: is an IPv6 link-local address, not a MAC. Enter the full address in IP address and choose the PC network adapter connected to that TV. The app adds the adapter's %scope number. You can also enter a scoped address directly. IPv4 addresses continue to work.

        Scan network checks IPv4 and IPv6 TV announcements on active network adapters. It also checks a bounded local IPv4 range to recover previously saved TVs by their saved MAC address when their IP changes. Network discovery cannot cross a router, guest Wi-Fi isolation or a disconnected link unless the network explicitly supports it. Devices that do not advertise and have no reachable control service may need Add TV and setup on the TV.

        Power on uses Android wake commands when ADB is reachable, or Wake-on-LAN packets using the TV's real active Ethernet/Wi-Fi MAC. A saved MAC can be used for a TV marked Other, but that does not add its native remote or power-off protocol. Wake packets are sent on ports 7 and 9. Sending a packet is not confirmation that a TV turned on.

        For reliable waking, use the TV remote to enable the model's network standby / Wake on LAN / Wake on Wi-Fi / Mobile TV On / Quick Start option where offered. Keep its network connection active in standby. These options and support vary by model. A physically disconnected or unsupported TV cannot be forced on by software.

        If a TV uses 192.168.0.x while the PC uses 192.168.1.x, the router must provide a working route, or connect the TV and PC to the same local network. Do not invent a new static address without checking the network. LG webOS controls require Pair LG and approval on the TV. Android controls require network debugging and approval on the TV.
        """, """
        عنوان IP وWake MAC مختلفان. العنوان الذي يبدأ بـfe80:: هو عنوان IPv6 محلي وليس MAC. أدخله كاملًا في «عنوان IP» واختر محوّل الكمبيوتر المتصل بشبكة التلفزيون، ليضيف البرنامج رقم النطاق بصيغة %scope. يمكنك إدخال العنوان مع النطاق مباشرة. تبقى عناوين IPv4 مدعومة.

        يفحص «فحص الشبكة» إعلانات التلفزيونات عبر IPv4 وIPv6 على المحوّلات النشطة. ويفحص نطاق IPv4 محليًا محدودًا لاستعادة عناوين التلفزيونات المحفوظة بمطابقة MAC إذا تغير IP. لا يعبر الاكتشاف الراوتر أو عزل شبكة الضيوف أو اتصالًا مفصولًا دون دعم الشبكة. قد تحتاج الأجهزة التي لا تعلن عن نفسها إلى «إضافة تلفزيون» وإعداد الاتصال على التلفزيون.

        يستخدم «تشغيل» أمر إيقاظ Android عندما يمكن الوصول إلى ADB، أو حزمة Wake-on-LAN باستخدام MAC الحقيقي للاتصال النشط في التلفزيون. يمكن استخدام MAC محفوظ لجهاز من النوع «أخرى»، دون إضافة بروتوكول ريموت أو إيقاف تشغيل خاص به. تُرسل الحزم إلى المنفذين 7 و9، ولا يعني إرسالها تأكيد تشغيل التلفزيون.

        فعّل بالريموت خيار الاستعداد الشبكي أو Wake on LAN أو Wake on Wi-Fi أو Mobile TV On أو Quick Start إن كان طراز التلفزيون يدعمه. يجب أن يبقى اتصال الشبكة نشطًا في الاستعداد. تختلف الخيارات والدعم حسب الجهاز، ولا يستطيع البرنامج تشغيل جهاز مفصول عن الكهرباء أو غير داعم.

        إذا كان التلفزيون على 192.168.0.x والكمبيوتر على 192.168.1.x، يلزم مسار صالح بين الشبكتين أو توصيل الجهازين بالشبكة المحلية نفسها. لا تضع عنوانًا ثابتًا عشوائيًا. يتطلب LG webOS «إقران LG» والموافقة على التلفزيون، ويتطلب Android تصحيح الشبكة والموافقة على الكمبيوتر.
        """)
    ];
}

