# PS Remote Sub

**ภาษาไทย** | [English](#english)

โปรแกรม Windows โอเพนซอร์ส (MIT) ที่อ่านซับไตเติลจากหน้าต่าง **PS Remote Play** ด้วย OCR ของ Windows แปลเป็นภาษาไทยด้วย Google Translate (ค่าเริ่มต้น ฟรี ไม่ต้องตั้งค่า) หรือ AI ที่คุณเลือก (Claude, OpenAI, Gemini, Ollama หรือ endpoint ที่กำหนดเอง) แล้วแสดงเป็น overlay ใสคลิกทะลุทับเกม และบนหน้าเว็บในวง LAN สำหรับเปิดดูบนมือถือหรือแท็บเล็ต

> ไม่เกี่ยวข้องกับ Sony Interactive Entertainment / PlayStation และไม่ได้รับการรับรองจากบริษัทดังกล่าว ไม่ใช้โลโก้ของ Sony

## โหมดการเล่น

- **โหมด PC (overlay):** ซับแปลแสดงทับหน้าต่าง Remote Play บนจอเดียวกัน ใสและคลิกทะลุ ไม่แย่งโฟกัสจากเกม
- **โหมดมือถือ / แท็บเล็ต:** เล่นบนจอ PC แล้วเปิดหน้าเว็บของโปรแกรมบนมือถือ (เครือข่าย Wi-Fi เดียวกัน) เพื่ออ่านซับแปลบนอีกจอ
- **โหมดทีวี (Android TV):** เล่น PS5 บนทีวีผ่าน HDMI แล้วให้ซับไทยขึ้นทับภาพเกมบนทีวี ดูหัวข้อ **ซับบนทีวี (Android TV)**

## ติดตั้ง

ดาวน์โหลดจากหน้า **Releases** ของ GitHub (ต้องใช้ Windows 10 build 19041 ขึ้นไป, 64 บิต)

- **ตัวติดตั้ง (`PSRemoteSub-win-Setup.exe`)** — ติดตั้งระดับผู้ใช้ ไม่ต้องใช้สิทธิ์ admin และอัปเดตตัวเองได้ (โปรแกรมเช็ก release ใหม่จาก GitHub ตอนเปิด แล้วถามก่อนอัปเดต)
- **Portable (`PSRemoteSub-vX.Y.Z-portable-win-x64.zip`)** — แตก zip แล้วรัน `PSRemoteSub.App.exe` ไม่อัปเดตเอง

**SmartScreen:** เวอร์ชันนี้ยังไม่ได้เซ็นโค้ด Windows อาจขึ้นหน้าต่าง "Windows protected your PC" ให้กด **More info → Run anyway**

## ติดตั้งบน macOS

ต้องใช้ macOS 13 ขึ้นไป, Mac แบบ Apple Silicon (M1 ขึ้นไป) เท่านั้น และ **PS Remote Play สำหรับ Mac** ดาวน์โหลดจากหน้า **Releases** ของ GitHub

- **ตัวติดตั้ง (`PSRemoteSub-osx-Setup.pkg`)** — ติดตั้งแอปลง Applications และอัปเดตตัวเองได้ (เช็ก release ใหม่จาก GitHub ตอนเปิด แล้วถามก่อนอัปเดต)
- **Portable (`PSRemoteSub-vX.Y.Z-portable-osx-arm64.zip`)** — แตก zip แล้วย้าย `PS Remote Sub.app` ไปไว้ที่ใดก็ได้ ไม่อัปเดตเอง

**เปิดครั้งแรก:** แอปยังไม่ผ่าน notarization ของ Apple macOS จึงอาจไม่ยอมเปิด ให้ไปที่ **System Settings → Privacy & Security** เลื่อนลงแล้วกด **Open Anyway** ข้างชื่อแอป แล้วยืนยันอีกครั้ง

**สิทธิ์ที่ macOS จะถาม:**
- **Screen Recording** — ใช้อ่านซับจากหน้าต่าง PS Remote Play (โหมด Remote Play)
- **Camera และ Microphone** — เมื่อใช้ Capture card (macOS นับ capture card เป็นกล้อง)
- **Local Network** — เมื่อเชื่อมต่อทีวี Android TV

หลังอัปเดตแอป macOS อาจถามสิทธิ์เหล่านี้อีกครั้ง

บน Mac แอปเป็นแอปบน menu bar (ไม่มีไอคอนใน Dock) หน้าเว็บสำหรับมือถืออยู่ที่พอร์ต **5055** (Windows ใช้ 5000 แต่ macOS ใช้พอร์ต 5000 กับ AirPlay Receiver) ปุ่มลัดใช้ **⌃⌥⇧ + ตัวอักษร** (Control+Option+Shift): ⌃⌥⇧S ปรับกรอบ OCR / ซับไทย, ⌃⌥⇧O เปิด/ปิด overlay, ⌃⌥⇧P หยุด/เริ่มอ่านชั่วคราว, ⌃⌥⇧F บันทึกภาพกรอบซับ (ใช้ดีบัก OCR), ⌃⌥⇧D เปิด/ปิดกรอบ debug

## ใช้งานครั้งแรก

1. เลือกวิธีแปล (ข้ามได้ ค่าเริ่มต้นคือ Google Translate ใช้ได้ทันทีโดยไม่ต้องตั้งค่า): ตั้งค่า → แท็บ **การแปล** ถ้าอยากได้คำแปลที่เป็นธรรมชาติกว่าและแปลชื่อตัวละครให้ ให้เลือก **AI** แล้วเลือกผู้ให้บริการ ใส่ API key (Ollama ไม่ต้องใช้ key) แล้วกด **ทดสอบ** (ดูหัวข้อ **การแปล: Google Translate และ AI** ด้านล่าง)
2. เปิด PS Remote Play และเริ่มสตรีมเกม
3. กด `Ctrl+Alt+Shift+S`
4. ลากเมาส์ครอบบริเวณที่ซับปรากฏ จะได้กรอบ OCR (สีเขียว) และกรอบซับไทย (สีส้ม) ลากกรอบส้มไปวางทับซับเดิมแล้วย่อขยายให้พอดี กด Enter เพื่อบันทึก ซับไทยจะขึ้นในกรอบส้ม
5. เล่นต่อได้เลย โปรแกรมอยู่ที่ไอคอนใน system tray (คลิกขวา: หยุด/เริ่มอ่าน, ปรับกรอบ OCR / ซับไทย, แสดง overlay, แสดงกรอบ debug, เปิดหน้าเว็บ, ตั้งค่า, ออก)

## เปิดดูบนมือถือ

1. ในหน้าตั้งค่า กดปุ่ม **เปิด firewall ให้เครื่องอื่นเข้าได้** (ขอสิทธิ์ admin หนึ่งครั้ง)
2. ตั้งเครือข่าย Wi-Fi ของ Windows เป็น **Private** (ถ้าเป็น Public มือถือจะเข้าไม่ได้)
3. สแกน **QR code** ในหน้าตั้งค่าด้วยมือถือ หรือเปิดที่อยู่ที่แสดง (พอร์ตเริ่มต้น 5000)
4. แตะปุ่ม **แตะเพื่อเริ่ม (กันจอดับ)** บนหน้าเว็บ เพื่อไม่ให้จอมือถือดับระหว่างเล่น

ถ้า Windows ถามเรื่อง firewall ให้กด Allow (Private)

### มือถือเข้าไม่ได้ (เคยกด Cancel ตอน Windows ถามเรื่อง firewall)

ตอนกด Cancel Windows จะจำไว้ว่าให้บล็อกโปรแกรมนี้ แก้ได้ดังนี้:

1. เปิด **Windows Defender Firewall** → **Allow an app or feature through Windows Defender Firewall**
2. กด **Change settings** แล้วหา **PSRemoteSub.App** ในรายการ
3. ติ๊กช่อง **Private** ของ PSRemoteSub.App แล้วกด **OK**

ถ้ายังเข้าไม่ได้ ให้เปิด **Advanced settings** → **Inbound Rules** แล้วปิด (Disable) กฎแบบ Block ที่ชื่อ PSRemoteSub.App

## ซับบนทีวี (Android TV)

เล่น PS5 บนทีวีโดยตรง แล้วให้ซับไทยขึ้นทับภาพเกมบนทีวี

📄 **[คู่มือการใช้งานโหมดทีวี (PDF)](docs/tv-mode-manual.pdf)** — คู่มือฉบับเต็มสำหรับผู้ใช้ ตั้งแต่ต่อสายจนถึงแก้ปัญหา

```
PS5 ──HDMI──► ทีวี (เล่นเกมบนจอทีวี)
 └─ Remote Play ─► PC: PS Remote Sub (OCR + แปล) ──LAN──► แอปบนทีวี วาดกล่องซับไทยทับเกม
```

- ต่อ PS5 เข้า HDMI ของทีวีตามปกติ และเปิด **PS Remote Play บน PC** ไว้ด้วย PC ใช้อ่านซับจาก Remote Play เท่านั้น จะเอาหน้าต่างนั้นไปไว้หลังหน้าต่างอื่นหรืออีกจอก็ได้ แต่ห้ามย่อ และปิด overlay บน PC ได้เพราะไม่ได้ดูบนจอ PC
- แอปบนทีวีแค่วาดสิ่งที่ PC ส่งมา: ซับไทยเป็นกล่องทึบทับซับอังกฤษของเกม (หรือเป็นแถบที่ขอบล่าง) แอปไม่ได้อ่านภาพจาก HDMI เพราะ Android ไม่อนุญาต
- **ต้องต่อจอยเข้า PC** (ไม่ใช่เข้า PS5) เพราะการกดปุ่มผ่านทาง Remote Play ส่วน PS5 ยังส่งภาพออก HDMI ไปทีวีตามเดิม
- **ซับไทยขึ้นช้ากว่าซับในเกมเล็กน้อย:** ต้องรอ OCR กับตัวแปลก่อน ซับอังกฤษจึงโผล่ให้เห็นสั้น ๆ แล้วกล่องไทยค่อยมาปิดทับ (รูปแบบ **ทับซับอังกฤษ**) ความช้าขึ้นกับความเร็วของตัวแปลที่เลือก
- PC กับทีวีต้องอยู่ในวง LAN เดียวกัน

### ตั้งค่าทีวีครั้งแรก (ทำครั้งเดียว)

ขั้นตอนเดียวกับข้อความในแท็บ **ทีวี** ของโปรแกรม:

1. บนทีวี: **ตั้งค่า → ระบบ → เกี่ยวกับ** กดที่ **บิลด์ระบบปฏิบัติการ Android TV** (Android TV OS build) 7 ครั้ง
2. กลับไปที่ **ระบบ → ตัวเลือกสำหรับนักพัฒนาซอฟต์แวร์** (Developer options) แล้วเปิด **การแก้ไขข้อบกพร่อง USB** (USB debugging) และ **การแก้ไขข้อบกพร่องผ่านเครือข่าย** (Network debugging)
3. ในโปรแกรมบน PC: ตั้งค่า → แท็บ **ทีวี** กด **ติดตั้งบนทีวี** ช่อง **IP ทีวี** เว้นว่างได้ โปรแกรมค้นหาทีวีในวง LAN เอง ถ้าหาไม่เจอให้ใส่ IP ของทีวี (ดูได้ที่ ตั้งค่า → เครือข่ายของทีวี)
4. ถ้าทีวีถามว่าอนุญาตให้แก้ไขข้อบกพร่องจากคอมเครื่องนี้ไหม ให้ติ๊ก **อนุญาตเสมอ** แล้วกด OK (โปรแกรมรอได้ 2 นาที)

โปรแกรมจะติดตั้งแอป **PSRS TV Overlay** แล้วเปิดบริการการเข้าถึง (Accessibility) ของแอปให้เอง โดยเพิ่มต่อท้ายรายการเดิม ไม่ทับบริการอื่นที่เปิดอยู่ ถ้าเปิดบริการให้ไม่ได้ ให้เปิดเองที่ ตั้งค่า → การเข้าถึง → PSRS TV Overlay

ถ้าบนทีวีมีแอปที่เซ็นด้วยกุญแจอื่นอยู่ โปรแกรมจะแจ้งให้ถอนแอป PSRS TV Overlay บนทีวีก่อน แล้วติดตั้งใหม่

### แท็บ ทีวี ในหน้าตั้งค่า

| รายการ | ใช้ทำอะไร |
|---|---|
| **ส่งซับไปทีวี** | เปิด/ปิดการส่งซับ ปิดแล้วทีวีไม่แสดงอะไร สถานะบอกว่า **ทีวีเชื่อมต่ออยู่** หรือ **ยังไม่พบทีวี** |
| **รูปแบบ** | **ทับซับอังกฤษ** วางกล่องไทยตรงที่ overlay บน PC จะวาด (ตามกรอบซับของแต่ละโซน) หรือ **แถบขอบล่าง** รวมซับทุกโซนเป็นแถบเดียวที่ขอบล่าง (ปรับ **ระยะจากขอบล่าง** ได้) |
| **ขนาดตัวอักษร** | ขนาดข้อความบนทีวี |
| **ความทึบของกล่อง** | ยิ่งทึบยิ่งบังซับอังกฤษมิด ยิ่งใสยิ่งเห็นภาพเกมทะลุ |
| **ปรับตำแหน่งบนทีวี** | ใช้เมื่อทีวีขยายหรือตัดขอบภาพ (overscan) ทำให้กล่องไม่ตรง ทีวีจะขึ้นกรอบสีขาวกับกล่องตัวอย่าง ปรับ **ขนาด**, **เลื่อนซ้าย–ขวา**, **เลื่อนขึ้น–ลง** จนกรอบตรงขอบภาพเกม กด **คืนค่าเดิม** เพื่อเริ่มใหม่ (กรอบปิดเองเมื่อปิดหน้าต่างปรับ) ค่าเก็บต่อ PC สำหรับทีวีเครื่องเดียว |
| **ติดตั้งบนทีวี** | ติดตั้งหรือติดตั้งซ้ำแอปบนทีวี ดูหัวข้อด้านบน |

ปุ่มลัดหยุด/เริ่มอ่านชั่วคราว (`Ctrl+Alt+Shift+P`) มีผลกับทีวีด้วย ส่วนการเปิด/ปิด overlay บน PC ไม่มีผลกับทีวี

### ซับหายเองเมื่อทีวีไม่ได้อยู่ที่ PS5

แอปบนทีวีวาดเฉพาะตอนที่ทีวีแสดงช่อง HDMI ของ PS5 ถ้าสลับไปหน้าหลัก YouTube ตั้งค่า หรืออินพุตอื่น กล่องซับจะหายทันที และกลับมาเมื่อสลับกลับ ใช้ได้กับทีวี **Sony BRAVIA / Google TV** เท่านั้น (อ่านจากแอปอินพุตของ Sony) ทีวียี่ห้ออื่นยังไม่รองรับ

### อัปเดตแอปบนทีวีอัตโนมัติ

ทำตอนเปิดโปรแกรมเท่านั้น (รวมถึงหลังโปรแกรมอัปเดตตัวเอง) และเฉพาะเมื่อเปิด **ส่งซับไปทีวี** ไว้ และทีวีเคยติดตั้งจากแท็บ **ทีวี** ของเครื่องนี้ ถ้าทีวีตอบทาง ADB และแอปบนทีวีเก่ากว่าตัวที่แถมมากับโปรแกรม โปรแกรมจะติดตั้งทับให้เองเงียบ ๆ ที่แจ้งเตือนมีสองกรณีเท่านั้น: ทีวียังไม่อนุญาตเครื่องนี้ (ให้กด **ติดตั้งบนทีวี** อีกครั้ง) และแอปบนทีวีเซ็นด้วยกุญแจอื่น (ถอนแอปบนทีวีก่อน แล้วติดตั้งใหม่) ความล้มเหลวอื่น เช่น ทีวีปิดอยู่หรือเชื่อมต่อไม่ได้ จะบันทึกไว้ใน log เท่านั้น ทีวีต้องเปิดอยู่และยังเปิด Network debugging ไว้

### Firewall และเครือข่าย

ทีวีเป็นฝ่ายเชื่อมมาที่พอร์ตเว็บของ PC (ค่าเริ่มต้น 5000) จึงต้องตั้งเหมือนตอน **เปิดดูบนมือถือ**: ตั้งเครือข่ายของ Windows เป็น **Private** และกด **เปิด firewall ให้เครื่องอื่นเข้าได้** ในหน้าตั้งค่า (หรือกด Allow แบบ Private ตอน Windows ถาม) ไม่งั้น Windows Firewall จะบล็อกทีวี และสถานะจะค้างที่ **ยังไม่พบทีวี** โปรแกรมประกาศตัวในวง LAN ด้วย UDP broadcast พอร์ต 47610 ทุก 2 วินาที ทีวีฟังพอร์ตนี้เพื่อหา PC เอง (ไม่ต้องใส่ IP ฝั่งทีวี) แล้วอ่านซับจาก `/tv/events` ของเว็บเซิร์ฟเวอร์ตัวเดียวกับที่ใช้กับมือถือ

### สำหรับผู้พัฒนา: แอปทีวีมาจากไหน

ตอนกด **ติดตั้งบนทีวี** และตอนเปิดโปรแกรม (อัปเดตอัตโนมัติ) โปรแกรมจะดู GitHub release ล่าสุดของ repo `psrs-tv-overlay` (owner เดียวกับ `RepositoryUrl` ของโปรแกรม) ถ้ามีไฟล์ `psrs-tv-overlay-<versionCode>.apk` ที่ versionCode ใหม่กว่าตัวที่แถมมา จะดาวน์โหลดมาไว้ที่ `tv/` ในโฟลเดอร์ข้อมูล (`%AppData%\PSRemoteSub\tv\` หรือ `~/Library/Application Support/PSRemoteSub/tv/`) โดยตรวจขนาดกับ SHA-256 ให้ตรงกับที่ GitHub บอก แล้วใช้ไฟล์นั้น ถ้าโหลดไม่ได้ (ไม่มีเน็ต, repo ยังเป็น private, GitHub ล่ม) จะใช้ไฟล์ที่เคยโหลดไว้ หรือถ้าไม่มีก็ใช้ตัวที่แถมในโฟลเดอร์ `tv/` ข้างโปรแกรม build ที่ไม่มี `RepositoryUrl` (build เองในเครื่อง) จะไม่ติดต่อ GitHub เลย ส่วนการออก release ของแอปทีวี ดูหัวข้อ **Release** ใน README ของ `psrs-tv-overlay`

ไฟล์ที่แถมมากับโปรแกรม: โฟลเดอร์ `tv/` เก็บของที่ใช้ติดตั้งแอปบนทีวี: `adb.exe` พร้อม DLL (Android SDK Platform-Tools 37.0.3), `psrs-tv-overlay.apk` (เซ็นด้วยกุญแจ release) และ `apk-version.txt` (versionCode ของ APK ที่แถม ใช้เทียบเพื่ออัปเดตอัตโนมัติ) โฟลเดอร์นี้ถูกคัดลอกไปอยู่ข้างโปรแกรมตอน build/publish adb ที่แถมใช้ server ของตัวเองที่พอร์ต 5038 (ไม่ใช่ 5037 ของ adb ตัวอื่นในเครื่อง) และปิด server นั้นทุกครั้งหลังติดตั้งหรืออัปเดตเสร็จ

อัปเดต APK ที่แถม (build แอปทีวีจาก repo `psrs-tv-overlay` ด้วยกุญแจ release ตรวจว่าลายเซ็นตรงกับกุญแจที่กำหนด แล้วเขียนทับ `tv/psrs-tv-overlay.apk` กับ `tv/apk-version.txt`):

```
powershell -ExecutionPolicy Bypass -File tools/UpdateTvApk.ps1 -TvRepo <path ของ psrs-tv-overlay>
```

ปกติไม่ต้องทำ: ผู้ใช้ได้แอปทีวีใหม่จาก GitHub release อยู่แล้ว อัปเดตตัวที่แถมก็ต่อเมื่ออยากให้ตัวสำรองใหม่ด้วย หลังรันให้ commit `tv/` ที่เปลี่ยน แล้วออกโปรแกรมรุ่นใหม่ ต้องเพิ่ม `versionCode` ในแอปทีวีก่อน build ไม่งั้นจะไม่ถูกมองว่าใหม่กว่า

**กุญแจ release:** อยู่ที่ `%USERPROFILE%\.psrs\psrs-tv-release.jks` (alias `psrs-tv`) รหัสผ่านอยู่ใน `keystore.properties` ที่รากของ repo `psrs-tv-overlay` (ไม่ถูก commit) **สำรองทั้งสองอย่างไว้นอกเครื่อง ถ้ากุญแจหาย จะอัปเดตแอปทับตัวที่ติดตั้งในทีวีไม่ได้** ผู้ใช้ต้องถอนแอปแล้วติดตั้งใหม่ และเปิดบริการการเข้าถึงใหม่

## ใช้ Capture card แทน Remote Play

ถ้าต่อ PS5 เข้า capture card (USB) โปรแกรมอ่านซับจากภาพของการ์ดแทนหน้าต่าง Remote Play ได้ ลองกับการ์ดแบบ MS2130 แล้ว

1. เลือกแหล่งภาพที่ ตั้งค่า → แท็บ **แหล่งภาพ** เป็น **Capture card** แล้วเลือก **อุปกรณ์** (กด **รีเฟรช** ถ้าไม่เห็นการ์ด) เมื่อเริ่มโปรแกรมในโหมดนี้ หน้าต่าง capture จะเปิดเอง และเปิดได้อีกจากเมนูที่ไอคอน tray (**เปิดจอ capture card**) ส่วนเลือก **Remote Play** เพื่อกลับไปใช้แบบเดิม
2. **ปิด HDCP บน PS5** (ตั้งค่า → ระบบ → HDMI → เปิดใช้งาน HDCP) ไม่งั้นการ์ดจะไม่ได้รับภาพ
3. ถ้าภาพซีดหรือออกเทา ให้ปิด **HDR บน PS5** การ์ดอย่าง MS2130 เป็น 8 บิต เวอร์ชันนี้ไม่รองรับ HDR
4. เปิดสิทธิ์ใน Windows (Settings → Privacy & security → Camera / Microphone): **Let desktop apps access your camera** และ **Let desktop apps access your microphone** ไม่งั้นเปิดการ์ดไม่ได้
5. ปิด **Listen to this device** ของช่องเสียงเข้าของการ์ด (Sound → Recording → คุณสมบัติของการ์ด → Listen) ไม่งั้นเสียงจะดังซ้อนสองครั้ง เพราะโปรแกรมส่งเสียงของการ์ดออกลำโพงเอง
6. กด `F11` หรือดับเบิลคลิกที่ภาพเพื่อเต็มจอ กด `Esc` เพื่อออก แถบเครื่องมือมีปุ่ม **ปิดเสียง** และ **เต็มจอ (F11)**
7. **ขนาดภาพ:** **อัตโนมัติ** เลือกขนาดใหญ่สุดที่ 60 fps (การ์ดที่ทดสอบได้ 1080p60) ถ้าอยากได้ภาพคมขึ้นเลือก 1440p50 ได้
8. หลังเปลี่ยนแหล่งภาพ ให้ตรวจกรอบ OCR / ซับอีกครั้งด้วย `Ctrl+Alt+Shift+S` เพราะขนาดภาพต่างจาก Remote Play
9. ถ้าเครื่องนี้เริ่ม capture ไม่ได้ ตัวเลือก **Capture card** จะกดไม่ได้ และโปรแกรมยังใช้กับ Remote Play ได้ตามปกติ (ดูสาเหตุใน log)

## ติดตั้งภาษา OCR

โปรแกรมใช้ OCR ที่มากับ Windows ต้องมี language pack ของภาษาในเกม ถ้ายังไม่มี โปรแกรมจะเตือนว่า **ยังไม่ได้ติดตั้งภาษา OCR นี้** ให้กดปุ่ม **ติดตั้ง** ที่แถวภาษานั้นในหน้าตั้งค่า (ขอ UAC แล้วรัน `Add-WindowsCapability -Online -Name Language.OCR~~~<tag>~0.0.1.0`) รองรับ อังกฤษ (`en-US`), ญี่ปุ่น (`ja-JP`), จีน (`zh-CN`), เกาหลี (`ko-KR`) ภาษาปลายทางเริ่มต้นเป็นไทย เปลี่ยนได้ในตั้งค่า

## ปุ่มลัด

| ปุ่ม | คำสั่ง |
|---|---|
| `Ctrl+Alt+Shift+S` | ปรับกรอบ OCR / ซับไทย |
| `Ctrl+Alt+Shift+O` | เปิด/ปิด overlay |
| `Ctrl+Alt+Shift+P` | หยุด/เริ่มอ่านชั่วคราว |
| `Ctrl+Alt+Shift+F` | บันทึกภาพกรอบซับ (ใช้ดีบัก OCR) ไปที่ `%AppData%\PSRemoteSub\captures\` |
| `Ctrl+Alt+Shift+D` | เปิด/ปิดกรอบ debug (เขียว = OCR, ส้ม = ซับไทย) |

เปลี่ยนปุ่มลัดได้ในหน้าตั้งค่า

## วิธีทำงาน

จับภาพหน้าต่าง Remote Play (Windows Graphics Capture) → OCR เฉพาะกรอบที่เลือกทุก ~300 ms → ตัวกรองความนิ่ง (ข้อความต้องเหมือนกัน ≥ 90% สองรอบติดกัน และไม่ซ้ำกับซับล่าสุด) → หาในคลังคำแปลก่อน ถ้าไม่มีจึงส่งให้ Google Translate หรือ AI ที่เลือกแปล (โหมด AI ส่งซับบรรทัดก่อนหน้าเป็นบริบทไปด้วย) (มี cache แปลทีละบรรทัด ถ้าซับเปลี่ยนเร็วกว่าที่ตัวแปลตอบ บรรทัดใหม่จะรอคิวแล้วแปลตามลำดับทุกบรรทัด รอได้สูงสุด 3 บรรทัด เกินนั้นบรรทัดที่รอนานที่สุดจะไม่ถูกแปล) → แสดงบน overlay และส่งไปหน้าเว็บ ซับหายไปเมื่อไม่พบข้อความ 1.5 วินาที

บน overlay ซับแต่ละบรรทัดค้างอย่างน้อย 3 วินาที (ปรับได้ 0–10 ที่ **เวลาค้างขั้นต่ำ (วินาที)** ในแท็บ **โปรไฟล์**) ถ้าบรรทัดใหม่มาก่อนครบเวลาจะรอคิว (รอได้สูงสุด 2 บรรทัด เกินนั้นบรรทัดที่รอนานที่สุดถูกข้าม) ตั้ง 0 = บรรทัดใหม่แทนที่ทันที หน้าเว็บไม่มีเวลาค้าง: แสดงคำแปลแต่ละบรรทัดทันทีที่แปลเสร็จ ตามลำดับ

## การแปล: Google Translate และ AI

เลือกโหมดที่ ตั้งค่า → แท็บ **การแปล** มี 2 โหมด:

- **Google Translate (ฟรี ไม่ต้องตั้งค่า):** ค่าเริ่มต้นของการติดตั้งใหม่ ไม่ต้องมี API key ส่งเฉพาะข้อความซับไปแปล (ไม่ส่งบริบทหรือชื่อเกม) และไม่แปลชื่อคนพูด: ชื่อที่คลังหรือโปรไฟล์มีคำไทยให้จะแสดงเป็นไทย ไม่มีก็แสดงชื่ออังกฤษ โปรแกรมใช้ endpoint ที่ไม่เป็นทางการของ Google (`translate.googleapis.com` แล้วสำรองที่ `clients5`) ซึ่ง Google อาจเปลี่ยนหรือจำกัดเมื่อไรก็ได้ ถ้าใช้ไม่ได้ทั้งสองทาง โปรแกรมจะหยุดแปล 30 วินาทีแล้วลองใหม่เอง สถานะบน tray ในหน้าตั้งค่า และหน้าเว็บจะขึ้นว่า **แปลไม่ได้ชั่วคราว — จะลองใหม่ใน 30 วินาที** ระหว่างนั้นซับขึ้นเป็นข้อความต้นฉบับพร้อม **(แปลไม่ได้)**
- **AI (ต้องมี API key, Ollama หรือ router):** ได้คำแปลที่เป็นธรรมชาติกว่า ใช้บริบทบรรทัดก่อนหน้ากับชื่อเกม และแปลชื่อตัวละครให้ ผู้ให้บริการแบบ cloud อาจคิดเงินตามการใช้งาน ช่องของ AI (ผู้ให้บริการ API key โมเดล บริบท ปุ่มทดสอบ URL ขั้นสูง คิว/ขนาน) จะโผล่เฉพาะเมื่อเลือกโหมดนี้

คลังคำแปลถูกค้นก่อนในทั้งสองโหมด (ภาษาอังกฤษ → ไทยเท่านั้น) โปรไฟล์เริ่มต้น (**ค่าเริ่มต้น**) ใช้คลัง `gta6` ติดตั้งใหม่จะเริ่มที่ Google ส่วนคนที่เคยตั้งค่า AI ไว้ในเวอร์ชันก่อน หลังอัปเดตจะยังใช้ AI ต่อ คนอื่นได้ Google

### โหมด AI

ถ้าเลือก AI แต่ยังไม่ได้ตั้งค่า โปรแกรมยังอ่านซับได้ แต่ซับจะขึ้นเป็นข้อความต้นฉบับพร้อม **(แปลไม่ได้)** และสถานะจะบอกว่าขาดอะไร (เช่น **ยังไม่ได้ใส่ API key — ใส่ได้ที่ ตั้งค่า → การแปล**) ถ้าเคยตั้งค่า AI ไว้ในเวอร์ชันก่อน ผู้ให้บริการ key และโมเดลเดิมยังอยู่ครบ

ซับแต่ละบรรทัดจะขึ้นเมื่อ AI แปลเสร็จ ระหว่างรอ บรรทัดเดิมยังค้างอยู่บนจอ ถ้า AI แปลไม่ได้ ซับจะขึ้นเป็นข้อความต้นฉบับพร้อม **(แปลไม่ได้)** แล้วโปรแกรมจะลองใหม่เอง: AI ล่มหรือตอบผิดรูปแบบ ลองใหม่หลัง 30 วินาที ถูกจำกัดการใช้งาน หลัง 60 วินาที ส่วน key ไม่ถูกต้อง ไม่พบโมเดล หรือเครดิตหมด จะหยุดจนกว่าจะแก้การตั้งค่า ข้อผิดพลาดของ AI แสดงใน tooltip ของไอคอน tray ในหน้าตั้งค่า (**สถานะ AI**) และบนแถบสถานะของหน้าเว็บ และถ้า API key ถูกปฏิเสธหรือเครดิตหมดจะมีการแจ้งเตือนขึ้นหนึ่งครั้งด้วย

ผู้ให้บริการ (**ผู้ให้บริการ**) และที่ขอ API key:

| ผู้ให้บริการ | ขอ key / ติดตั้งที่ | โมเดลเริ่มต้น |
|---|---|---|
| Claude | Anthropic Console (console.anthropic.com) | `claude-haiku-5-5` |
| OpenAI | OpenAI Platform (platform.openai.com) | `gpt-5-mini` |
| Gemini | Google AI Studio (aistudio.google.com) | `gemini-3.5-flash-lite` |
| Ollama | ติดตั้งจาก ollama.com แล้วรัน `ollama pull <ชื่อโมเดล>` | ไม่มี (เลือกเอง) |
| กำหนดเอง | ปลายทางที่เข้ากันได้กับ OpenAI API (ใส่ URL เอง) | ไม่มี (ใส่เอง) |

- **ค่าใช้จ่าย:** Claude และ OpenAI คิดเงินตามการใช้งานกับบัญชีของคุณเอง Gemini มี free tier (ดูเงื่อนไขและโควตาปัจจุบันใน Google AI Studio) Ollama ฟรีและรันบนเครื่อง ผู้ให้บริการแบบ cloud คิดตามจำนวนซับที่แปล ยิ่งเล่นนานยิ่งใช้มาก
- **โมเดล:** ช่อง **โมเดล** แก้ได้ทุกผู้ให้บริการ Ollama ไม่มีค่าเริ่มต้น กด **ดึงรายชื่อโมเดล** เพื่อเลือกจากโมเดลที่ pull ไว้แล้ว ซับต้องแปลเร็ว โมเดลขนาดเล็กหรือรุ่นเบา (lite / mini / haiku) จึงเหมาะกว่า
- **API key:** ใส่ในช่อง **API key** แล้วกด **ทดสอบ** เพื่อลองแปลประโยคตัวอย่าง
- **บริบท:** ส่งซับบรรทัดก่อนหน้าพร้อมคำแปลไปด้วย ค่าเริ่มต้น 4 บรรทัด ปรับได้ 0–10 ที่ **บริบท**
- **ชื่อเกม (ช่วย AI):** อยู่ที่แท็บ **โปรไฟล์** ใส่ชื่อเกมแยกตามโปรไฟล์ของแต่ละเกมได้ เพื่อให้ AI เลือกคำศัพท์และชื่อเฉพาะได้ตรงขึ้น
- **Ollama บนเครื่องอื่นในวง LAN:** เปิด **ขั้นสูง** แล้วเปลี่ยน **URL** จาก `http://localhost:11434/v1` เป็นที่อยู่ของเครื่องนั้น
- **การเก็บ key:** เข้ารหัสด้วย Windows DPAPI ผูกกับผู้ใช้ Windows คนปัจจุบัน และไม่ถูกบันทึกลง log ถ้าคัดลอก `settings.json` ไปเครื่องอื่น key จะไม่ติดไปด้วย โปรแกรมจะแสดง **ยังไม่ได้ใส่ API key** ให้ใส่ key ใหม่ที่เครื่องนั้น

## คลังคำแปล

คลังคำแปลคือชุดคำแปลที่เตรียมไว้ต่อเกม (ตอนนี้มี `gta6`) ซับที่อยู่ในคลังจะขึ้นทันทีโดยไม่ต้องรอ Google หรือ AI ส่วนบรรทัดอื่นโหมดที่เลือกแปลให้ตามปกติ

- **ผู้เล่น:** เลือกคลังที่ ตั้งค่า → แท็บ **โปรไฟล์** ช่อง **คลังคำแปล** (แยกตามโปรไฟล์ของแต่ละเกม เลือก **ไม่ใช้คลัง** เพื่อปิด) โปรแกรมเช็กและดาวน์โหลดคลังรุ่นใหม่เองเมื่อมี ปุ่ม **เปิดโฟลเดอร์คลังของเกม** เปิดโฟลเดอร์คลังของเกมนั้น ถ้าติ๊ก **จำคำแปล AI ของโซนนี้ไว้ใช้ซ้ำ** ที่โซนไหน โปรแกรมจะจำคำแปลของ AI ในโซนนั้นไว้ในหน่วยความจำขณะเปิดโปรแกรม บรรทัดที่ซ้ำจึงไม่ถูกส่งให้ AI อีก ถ้าป้ายร้านหรือทะเบียนรถในกรอบ OCR ทำให้ต้องรอตัวแปล ให้ติ๊ก **แปลเฉพาะบรรทัดที่มีชื่อคนพูด** (ใช้กับเกมที่ซับมีชื่อคนพูดเท่านั้น)
- **ผู้ดูแล:** เขียนไฟล์ `library/<game>/pending.jsonl` (ไม่ถูก commit) เอง (บรรทัดละ `{"en","th","speaker"?,"source"?}` เช่นแปลเองหรือใช้ AI) แล้วเพิ่มเข้าคลัง แพ็กด้วยเวอร์ชันเดียวกับ tag แล้ว commit `manifest.json` กับ `subtitles.jsonl` ที่อัปเดตแล้ว **ก่อน** push tag `lib-YYYYMMDD.N` (workflow `.github/workflows/library.yml` จะเทสต์ แพ็กซ้ำด้วยเวอร์ชันจาก tag ซึ่งได้ไฟล์เหมือนกัน และออกเป็น prerelease ให้เอง):

```
dotnet run --project tools/LibraryTool -- append gta6
dotnet run --project tools/LibraryTool -- pack 20261121.1 dist
dotnet test PSRemoteSub.sln
git add library/gta6/subtitles.jsonl library/gta6/manifest.json
git commit -m "chore(library): gta6 20261121.1"
git tag lib-20261121.1
git push origin main lib-20261121.1
```

ตรวจ/แก้ `pending.jsonl` ได้ก่อนสั่ง `append` ซึ่งเพิ่มเข้า `subtitles.jsonl` (ใช้ `source` ที่แก้ไว้ใน pending ถ้ามี ไม่งั้นเป็น `ai`) แล้วลบ `pending.jsonl` ส่วน `pack <version> <outDir>` แพ็กทุกเกมใน `library/` เป็น zip ใน `<outDir>` (`dist/` ไม่ถูก commit) และเขียน `manifest.json` ของแต่ละเกมใหม่ (เวอร์ชัน จำนวนบรรทัด และ sha256 ของ `subtitles.jsonl`) ต้อง commit `manifest.json` นี้ไว้ใน repo เพื่อให้คลังที่แจกมากับโปรแกรมรุ่นถัดไปตรงกับรุ่นล่าสุด

## ข้อจำกัด

- **ห้ามย่อหน้าต่าง Remote Play** — ถ้าย่อ Windows จะหยุดส่งภาพ โปรแกรมจะหยุดอ่านและเตือนว่า **หน้าต่าง Remote Play ถูกย่ออยู่** (บนหน้าเว็บ ไอคอน tray และหน้าตั้งค่า) จนกว่าจะเปิดหน้าต่างกลับมา ให้เลื่อนหน้าต่างไปไว้ด้านหลังหรืออีกจอแทน (บังได้ แต่ห้ามย่อ)
- โหมด Google ใช้ endpoint ที่ไม่เป็นทางการ อาจใช้ไม่ได้ชั่วคราวหรือถูกจำกัด และไม่แปลชื่อคนพูด โหมด AI ต้องตั้งค่าผู้ให้บริการก่อนจึงจะแปลได้ คำแปลขึ้นช้ากว่าซับในเกมตามความเร็วของตัวแปล (โมเดลเล็กหรือ Ollama บนเครื่องที่แรงพอจะเร็วกว่า)
- คุณภาพ OCR ขึ้นกับฟอนต์และพื้นหลังของซับในเกม

## ความเป็นส่วนตัว

- **ไม่มี telemetry** ไม่เก็บหรือส่งข้อมูลการใช้งาน
- ข้อมูลที่ออกจากเครื่องมีสองอย่างเท่านั้น: **ข้อมูลที่ส่งไปแปล** และ **การเช็กอัปเดตไปที่ GitHub** (เฉพาะตัวติดตั้ง) ข้อมูลที่ส่งไปแปลขึ้นกับโหมด: **โหมด Google Translate** ส่งข้อความซับไปที่ Google Translate (เฉพาะข้อความซับ ไม่ส่งบริบทหรือชื่อเกม) **โหมด AI** ส่งไปที่ผู้ให้บริการ AI ที่คุณเลือกเท่านั้น ไม่ส่งไป Google Translate
- ในโหมด AI ผู้ให้บริการที่เลือกจะได้รับ: ข้อความซับ (รวมชื่อผู้พูดที่อ่านได้ เพื่อแปลชื่อ), ซับบรรทัดก่อนหน้าพร้อมคำแปล และชื่อเกม ไม่ส่งอย่างอื่น ผู้ให้บริการแบบ cloud คิดค่าใช้จ่ายตามการใช้งาน ส่วน Ollama บนเครื่องตัวเองข้อมูลไม่ออกจากเครื่อง (ถ้าชี้ไป Ollama บนเครื่องอื่นในวง LAN ข้อมูลจะถูกส่งไปเครื่องนั้น)
- API key เก็บแบบเข้ารหัสด้วย Windows DPAPI และไม่ถูกบันทึกลง log
- การตั้งค่าเก็บที่ `%AppData%\PSRemoteSub\settings.json`
- บันทึกการทำงาน (log) อยู่ที่ `%AppData%\PSRemoteSub\log.txt` (ของเก่ากว่านั้นอยู่ใน `log.1.txt` ไฟล์ละไม่เกิน 1 MB) เก็บแค่เหตุการณ์และข้อผิดพลาด **ไม่มีข้อความซับหรือคำแปล** และไม่ถูกส่งไปไหน เวลาแจ้งปัญหาให้แนบไฟล์นี้มาด้วย

## Build จากซอร์ส

ต้องมี .NET 10 SDK บน Windows

```
dotnet build PSRemoteSub.sln
dotnet test PSRemoteSub.sln
dotnet run --project src/PSRemoteSub.App -f net10.0-windows10.0.19041.0
dotnet publish src/PSRemoteSub.App -c Release -f net10.0-windows10.0.19041.0 -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

แอปมีสอง target framework (Windows และ macOS) จึงต้องใส่ `-f net10.0-windows10.0.19041.0` ตอน `dotnet run` บน Windows

บน macOS: ต้องมี .NET 10 SDK และ Swift (Xcode Command Line Tools) แล้วรัน `tools/mac/run-dev.sh` เพื่อ build และเปิดแอปแบบ dev (ไม่ใช้ `dotnet run`) ส่วนเทสต์ `dotnet test PSRemoteSub.sln` รันบน Mac ไม่ได้ เพราะรวม `tests/PSRemoteSub.Windows.Tests` ที่รันได้เฉพาะ Windows ให้ build solution แล้วเทสต์ทีละโปรเจกต์:

```
dotnet build PSRemoteSub.sln
dotnet test tests/PSRemoteSub.Core.Tests --no-build
dotnet test tests/PSRemoteSub.Web.Tests --no-build
dotnet test tests/PSRemoteSub.MacOS.Tests --no-build
```

ผลลัพธ์อยู่ที่ `publish\PSRemoteSub.App.exe` (พร้อม `LICENSE` และ `THIRD-PARTY-NOTICES.md`) ตัว build ที่ออกจาก release workflow จะใส่ URL ของ repo ให้อัตโนมัติ (`-p:RepositoryUrl=...`) จึงเช็กอัปเดตได้ (เฉพาะตัวที่ติดตั้งด้วย Velopack) ส่วน build บนเครื่องจะไม่เช็กอัปเดตเลย เพราะ `<RepositoryUrl>` ใน `src/PSRemoteSub.App/PSRemoteSub.App.csproj` เว้นว่างไว้ ถ้าต้องการทดสอบ ให้ใส่ URL (เช่น `https://github.com/OWNER/PSRemoteSub`) หรือส่ง `-p:RepositoryUrl=...` ตอน publish

ออก release: push tag เช่น `v1.0.0` แล้ว `.github/workflows/release.yml` จะสร้างตัวติดตั้ง Velopack และ zip แบบ portable ส่วนเทสต์ OCR ใช้ `[OcrFact]` ซึ่งข้ามตัวเองตอนรันเมื่อเครื่องไม่มี language pack ภาษาอังกฤษ (`en-US`) เช่น runner ของ CI และรันจริงบนเครื่องที่มี จึงไม่ต้องใส่ filter

## สแกน secret

repo นี้ใช้ [gitleaks](https://github.com/gitleaks/gitleaks) กัน API key และ secret อื่นไม่ให้หลุดเข้า git

- **ในเครื่อง:** ติดตั้ง [pre-commit](https://pre-commit.com) (`brew install pre-commit` หรือ `pip install pre-commit`) แล้วรัน `pre-commit install` ครั้งเดียวต่อการ clone หลังจากนั้นทุก commit จะถูกสแกนก่อน ถ้าไม่ได้ติดตั้ง hook จะไม่มีอะไรตรวจในเครื่องเลย
- **CI:** workflow `gitleaks` สแกน history ทั้งหมดทุกครั้งที่ push และเปิด PR จึงจับได้แม้จะ commit ด้วย `--no-verify` หรือแก้ผ่านหน้าเว็บ
- **ถ้าแจ้งเตือนผิด:** ใส่ comment `gitleaks:allow` ท้ายบรรทัดนั้น หรือเพิ่ม path/ค่าไว้ใต้ `[allowlist]` ใน `.gitleaks.toml`
- **ถ้าสิ่งที่เจออยู่ใน history ที่ push ไปแล้ว** (CI แดงทุก branch): `gitleaks:allow` ใช้ได้เฉพาะเมื่ออยู่ใน commit เดียวกับบรรทัดนั้น ให้คัดลอกบรรทัด `Fingerprint:` จาก log ของ CI ไปใส่ในไฟล์ `.gitleaksignore` ที่ root ของ repo บรรทัดละหนึ่งรายการ (ถ้าเป็น secret จริง ให้ rotate ก่อน)
- **ถ้า secret จริงหลุดเข้า commit ไปแล้ว:** ให้ rotate key นั้นทันที การลบออกใน commit ถัดไปไม่พอ เพราะยังค้างอยู่ใน history

## สัญญาอนุญาต

MIT — ดู [LICENSE](LICENSE) และ [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)

---

<a id="english"></a>

# PS Remote Sub (English)

An open-source (MIT) Windows app that reads subtitles from the **PS Remote Play** window with Windows OCR, translates them to Thai with Google Translate (the default: free, nothing to set up) or the AI you choose (Claude, OpenAI, Gemini, Ollama or a custom endpoint), and shows them as a click-through overlay on top of the game and on a web page on your LAN for a phone or tablet. The target language can be changed in Settings.

> Not affiliated with, endorsed by, or sponsored by Sony Interactive Entertainment / PlayStation. No Sony logos are used.

## Play modes

- **PC mode (overlay):** the translated subtitle is drawn over the Remote Play window on the same screen. It is transparent, click-through, and never steals focus from the game.
- **Phone / tablet mode:** play on the PC and open the app's web page on your phone (same Wi-Fi) to read the translation on a second screen.
- **TV mode (Android TV):** play the PS5 on the TV over HDMI and get Thai subtitles drawn over the game on the TV. See **Subtitles on the TV (Android TV)**.

## Install

Download from the GitHub **Releases** page (Windows 10 build 19041 or later, 64-bit).

- **Installer (`PSRemoteSub-win-Setup.exe`):** per-user install, no admin rights, self-updating. On startup the app checks GitHub for a new release and asks before updating.
- **Portable (`PSRemoteSub-vX.Y.Z-portable-win-x64.zip`):** unzip and run `PSRemoteSub.App.exe`. Does not self-update.

**SmartScreen:** this build is not code-signed, so Windows may show "Windows protected your PC". Click **More info → Run anyway**.

## Install on macOS

Requires macOS 13 or later, an Apple Silicon Mac (M1 or newer) only, and **PS Remote Play for Mac**. Download from the GitHub **Releases** page.

- **Installer (`PSRemoteSub-osx-Setup.pkg`):** installs the app into Applications and self-updates (checks GitHub for a new release on startup and asks before updating).
- **Portable (`PSRemoteSub-vX.Y.Z-portable-osx-arm64.zip`):** unzip and move `PS Remote Sub.app` anywhere. Does not self-update.

**First launch:** the app is not notarized by Apple, so macOS may refuse to open it. Go to **System Settings → Privacy & Security**, scroll down and click **Open Anyway** next to the app's name, then confirm.

**Permissions macOS asks for:**
- **Screen Recording:** to read subtitles from the PS Remote Play window (Remote Play mode).
- **Camera and Microphone:** when using a capture card (macOS treats it as a camera).
- **Local Network:** to reach the Android TV.

After an app update macOS may ask for these again.

On the Mac the app is a menu bar app (no Dock icon). The phone web page is on port **5055** (Windows uses 5000, but macOS uses port 5000 for AirPlay Receiver). Hotkeys are **⌃⌥⇧ + a letter** (Control+Option+Shift): ⌃⌥⇧S edit the OCR and caption boxes, ⌃⌥⇧O toggle overlay, ⌃⌥⇧P pause / resume reading, ⌃⌥⇧F save the subtitle crop (for debugging OCR), ⌃⌥⇧D toggle the debug outlines.

## First run

1. Choose how to translate (optional: the default, Google Translate, works at once with nothing to set up): Settings → the **การแปล** ("Translation") tab. For more natural wording and translated character names, pick **AI**, then a provider, enter an API key (Ollama needs none) and click **ทดสอบ** ("Test"). See **Translation: Google Translate and AI** below.
2. Open PS Remote Play and start streaming a game.
3. Press `Ctrl+Alt+Shift+S`.
4. Drag a rectangle around where subtitles appear. This makes the OCR box (green) and the caption box (orange). Move and resize the orange box over the original subtitle, then press Enter. The Thai text is drawn in the orange box.
5. Play. The app lives in the system tray (right-click: pause/resume, edit the OCR and caption boxes, show overlay, show debug outlines, open web page, settings, exit).

## Phone mode

1. In Settings, click the firewall button (**เปิด firewall ให้เครื่องอื่นเข้าได้**, "open the firewall for other devices"). It asks for admin rights once.
2. Set your Windows Wi-Fi network profile to **Private**. On a Public network the phone cannot connect.
3. Scan the **QR code** in Settings with your phone, or open the address shown (default port 5000).
4. Tap the button on the web page (**แตะเพื่อเริ่ม (กันจอดับ)**, "tap to start (keep screen on)") so the phone screen does not turn off while you play.

If Windows asks about the firewall, click **Allow** (Private networks).

### The phone cannot connect (Cancel was clicked on the Windows firewall prompt)

Clicking Cancel makes Windows remember to block the app. To fix it:

1. Open **Windows Defender Firewall** → **Allow an app or feature through Windows Defender Firewall**.
2. Click **Change settings** and find **PSRemoteSub.App** in the list.
3. Tick its **Private** box and click **OK**.

If it still fails, open **Advanced settings** → **Inbound Rules** and disable any **Block** rule named PSRemoteSub.App.

## Subtitles on the TV (Android TV)

Play the PS5 on the TV directly and get the Thai subtitles drawn over the game picture on the TV.

📄 **[TV mode user manual (PDF, Thai)](docs/tv-mode-manual.pdf)** — the full step-by-step guide, from cabling to troubleshooting (Thai only).

```
PS5 ──HDMI──► TV (you play on the TV screen)
 └─ Remote Play ─► PC: PS Remote Sub (OCR + translate) ──LAN──► TV app draws Thai boxes over the game
```

- Connect the PS5 to the TV over HDMI as usual, and keep **PS Remote Play running on the PC**. The PC only reads subtitles from Remote Play, so you can cover that window with others or put it on another screen (never minimize it). You can turn the PC overlay off, since you are not looking at the PC screen.
- The TV app only draws what the PC sends: Thai boxes that cover the game's English subtitles (or a strip at the bottom). It does not read the HDMI picture, because Android does not allow that.
- **Connect the controller to the PC** (not the PS5): input goes through Remote Play, while the PS5 keeps sending its picture to the TV over HDMI.
- **The Thai text lags the game's subtitle a little:** OCR and the translator need time, so the English line shows briefly before the Thai box covers it (in **ทับซับอังกฤษ**, "cover English" style). How long depends on the translation mode you chose.
- The PC and the TV must be on the same LAN.

### One-time TV setup

The same steps as the text on the app's **ทีวี** ("TV") tab:

1. On the TV: **Settings → System → About**, press **Android TV OS build** 7 times.
2. Go back to **System → Developer options** and turn on **USB debugging** and **Network debugging**.
3. In the PC app: Settings → the **ทีวี** tab, click **ติดตั้งบนทีวี** ("Install on TV"). The **IP ทีวี** ("TV IP") field can stay empty and the app finds the TV on the LAN; if it cannot, type the TV's IP (see the TV's network settings).
4. If the TV asks whether to allow debugging from this computer, tick **Always allow** and press OK (the app waits up to 2 minutes).

The app installs **PSRS TV Overlay** and turns on its Accessibility service for you, by adding it to the existing list so other enabled services are not replaced. If it cannot enable it, turn it on yourself in Settings → Accessibility → PSRS TV Overlay.

If a copy signed with another key is already on the TV, the app asks you to uninstall PSRS TV Overlay from the TV first, then install again.

### The TV tab in Settings

| Item | What it does |
|---|---|
| **ส่งซับไปทีวี** ("Send subtitles to TV") | Turns sending on or off. Off, the TV shows nothing. The status says **ทีวีเชื่อมต่ออยู่** ("TV connected") or **ยังไม่พบทีวี** ("no TV found"). |
| **รูปแบบ** ("Style") | **ทับซับอังกฤษ** ("cover English") puts the Thai box where the PC overlay would draw it (each zone's caption box), or **แถบขอบล่าง** ("bottom strip") stacks every zone's lines in one strip at the bottom (set **ระยะจากขอบล่าง**, the distance from the bottom edge). |
| **ขนาดตัวอักษร** ("Text size") | Size of the text on the TV. |
| **ความทึบของกล่อง** ("Box opacity") | The more opaque, the better it hides the English line; the lighter, the more of the game shows through. |
| **ปรับตำแหน่งบนทีวี** ("Adjust position on TV") | For TVs that zoom or crop the picture (overscan) so the boxes do not line up. The TV shows a white frame and sample boxes; adjust **ขนาด** (size), **เลื่อนซ้าย–ขวา** (shift left/right) and **เลื่อนขึ้น–ลง** (shift up/down) until the frame matches the edges of the game picture. **คืนค่าเดิม** ("Reset") starts over. The frame closes when you close the window. Stored per PC, for one TV. |
| **ติดตั้งบนทีวี** ("Install on TV") | Installs or reinstalls the TV app (see above). |

The pause/resume hotkey (`Ctrl+Alt+Shift+P`) applies to the TV too; turning the PC overlay on or off does not.

### Auto-hide when the TV is not on the PS5

The TV app only draws while the TV shows the PS5's HDMI input. Switch to the home screen, YouTube, Settings or another input and the boxes disappear at once, then come back when you switch back. This works on **Sony BRAVIA / Google TV** only (it reads Sony's input app); other TV brands are not supported.

### TV app auto-update

This runs only when the PC app starts (including after it updates itself), and only when **ส่งซับไปทีวี** is on and the TV was installed from the **ทีวี** tab on this PC. If that TV answers over ADB and its app is older than the one bundled with the PC app, the PC app reinstalls it quietly. Only two failures show a notification: the TV no longer trusts this PC (click **ติดตั้งบนทีวี** again), and the TV app is signed with another key (uninstall it from the TV, then install again). Other failures, such as a TV that is off or unreachable, are only written to the log. The TV must be on with Network debugging still enabled.

### Firewall and network

The TV connects to the PC's web port (default 5000), so set it up as **Phone mode** needs: set the Windows network to **Private** and click **เปิด firewall ให้เครื่องอื่นเข้าได้** in Settings (or click Allow for Private networks when Windows asks). Otherwise Windows Firewall blocks the TV and the status stays at **ยังไม่พบทีวี**. The app announces itself on the LAN with a UDP broadcast on port 47610 every 2 seconds; the TV listens there to find the PC on its own (no IP to type on the TV) and then reads subtitles from `/tv/events` on the same web server the phone page uses.

### For developers: where the TV app comes from

On **ติดตั้งบนทีวี** and at startup (the auto-update), the app checks the latest GitHub release of the `psrs-tv-overlay` repo (same owner as the app's `RepositoryUrl`). When it has a `psrs-tv-overlay-<versionCode>.apk` newer than the bundled one, the app downloads it into `tv/` in the data folder (`%AppData%\PSRemoteSub\tv\` or `~/Library/Application Support/PSRemoteSub/tv/`), checks its size and SHA-256 against what GitHub reports, and uses it. When the download fails (offline, repo still private, GitHub down) it uses a previously downloaded file, else the one bundled in `tv/` next to the app. Builds without a `RepositoryUrl` (local builds) never contact GitHub. To release the TV app, see **Release** in the `psrs-tv-overlay` README.

Bundled with the app: The `tv/` folder holds what installs the TV app: `adb.exe` with its DLLs (Android SDK Platform-Tools 37.0.3), `psrs-tv-overlay.apk` (signed with the release key) and `apk-version.txt` (the bundled APK's versionCode, compared for the auto-update). It is copied next to the app on build/publish. The bundled adb runs its own server on port 5038 (not the 5037 one other adb copies use) and stops it after every install or update.

To refresh the bundled APK (builds the TV app from the `psrs-tv-overlay` repo with the release key, checks that the signature matches the expected key, and overwrites `tv/psrs-tv-overlay.apk` and `tv/apk-version.txt`):

```
powershell -ExecutionPolicy Bypass -File tools/UpdateTvApk.ps1 -TvRepo <path to psrs-tv-overlay>
```

Usually not needed: users get new TV app versions from the GitHub release. Refresh the bundled copy only to update the fallback; then commit the changed `tv/` and ship a new app release. Raise `versionCode` in the TV app before building, or it will not count as newer.

**Release key:** `%USERPROFILE%\.psrs\psrs-tv-release.jks` (alias `psrs-tv`); the passwords are in `keystore.properties` at the root of the `psrs-tv-overlay` repo (not committed). **Back both up off this PC. If the key is lost, installed TV apps can no longer be updated in place:** users must uninstall the app and install it again, then re-enable the Accessibility service.

## Using a capture card instead of Remote Play

If your PS5 is connected to a USB capture card, the app can read subtitles from the card's picture instead of the Remote Play window. Tested with an MS2130-type card.

1. In Settings → the **แหล่งภาพ** ("Picture source") tab, choose **Capture card** and pick the **อุปกรณ์** ("Device"; click **รีเฟรช**, "Refresh", if the card is missing). When the app starts in this mode the capture window opens by itself, and it can be opened again from the tray menu (**เปิดจอ capture card**, "open the capture card window"). Choose **Remote Play** to go back to the old way.
2. **Turn HDCP off on the PS5** (Settings → System → HDMI → Enable HDCP), or the card gets no picture.
3. If the picture looks grey or washed out, turn **HDR off on the PS5**. Cards like the MS2130 are 8-bit, and HDR is not supported in this version.
4. Allow camera and microphone access in Windows (Settings → Privacy & security → Camera / Microphone): turn on **Let desktop apps access your camera** and **Let desktop apps access your microphone**, or the card cannot be opened.
5. Turn off **Listen to this device** for the card's audio input (Sound → Recording → the card's properties → Listen), or the sound plays twice, because the app already plays the card's audio itself.
6. Press `F11` or double-click the picture for fullscreen; `Esc` leaves it. The toolbar has **ปิดเสียง** ("Mute") and **เต็มจอ (F11)** ("Fullscreen").
7. **ขนาดภาพ** ("Picture size"): **อัตโนมัติ** ("Automatic") picks the largest size at 60 fps (1080p60 on the tested card). Choose 1440p50 for a sharper picture.
8. After switching source, re-check the OCR and caption boxes with `Ctrl+Alt+Shift+S`, since the picture size differs from Remote Play.
9. If capture cannot start on this PC, the **Capture card** option is disabled and the app keeps working with Remote Play as usual (see the log for the reason).

## Installing OCR languages

OCR comes from Windows and needs the language pack for the game's language. If it is missing, the app warns that the OCR language is not installed; click **ติดตั้ง** ("install") on that language's row in Settings. It asks for UAC and runs `Add-WindowsCapability -Online -Name Language.OCR~~~<tag>~0.0.1.0`. Supported: English (`en-US`), Japanese (`ja-JP`), Chinese (`zh-CN`), Korean (`ko-KR`).

## Hotkeys

| Keys | Action |
|---|---|
| `Ctrl+Alt+Shift+S` | Edit the OCR and caption boxes |
| `Ctrl+Alt+Shift+O` | Toggle overlay |
| `Ctrl+Alt+Shift+P` | Pause / resume reading |
| `Ctrl+Alt+Shift+F` | Save the subtitle crop (for debugging OCR) to `%AppData%\PSRemoteSub\captures\` |
| `Ctrl+Alt+Shift+D` | Toggle the debug outlines (green = OCR, orange = caption box) |

Hotkeys can be changed in Settings.

## How it works

Capture the Remote Play window (Windows Graphics Capture) → OCR only the selected region every ~300 ms → stability filter (text must match at ≥ 90% on two consecutive reads and differ from the last subtitle shown) → look up the translation library first, then translate with Google Translate or the chosen AI (AI mode also sends the previous lines as context) (cached; one line at a time, and when subtitles change faster than the translator answers, new lines wait in order and every one is translated; at most 3 wait, beyond that the oldest waiting line is left untranslated) → overlay and web page. The subtitle clears after 1.5 s with no text.

On the overlay each line stays at least 3 s (0–10, **เวลาค้างขั้นต่ำ (วินาที)** ("minimum display time") on the **โปรไฟล์** tab). A newer line that comes sooner waits its turn (at most 2 wait; beyond that the oldest waiting line is skipped). 0 = replaced at once. The web page has no minimum time: it shows each line as soon as it is translated, in order.

## Translation: Google Translate and AI

Choose the mode in Settings → the **การแปล** ("Translation") tab. There are two:

- **Google Translate (free, nothing to set up):** the default on a fresh install. No API key. Only the subtitle text is sent (no context, no game title), and speaker names are not translated: a name that the library or the profile has a Thai form for shows in Thai, otherwise the English name shows. It uses Google's unofficial endpoints (`translate.googleapis.com`, with `clients5` as a fallback), which Google can change or limit at any time. If both fail, the app pauses for 30 seconds and retries on its own; the tray, Settings and the phone page show **แปลไม่ได้ชั่วคราว — จะลองใหม่ใน 30 วินาที** ("temporarily cannot translate, retrying in 30 seconds"), and meanwhile lines show in the original language marked **(แปลไม่ได้)**.
- **AI (needs an API key, Ollama or a router):** more natural wording; it uses the previous lines and the game title as context and translates character names. Cloud providers may bill per use. The AI fields (provider, API key, model, context, Test, advanced URL, queue/parallel) appear only when this mode is selected.

The translation library is looked up first in both modes (English to Thai only); the default profile (**ค่าเริ่มต้น**) uses the `gta6` library. A fresh install starts on Google. If you had AI set up in an earlier version, you stay on AI after updating; everyone else gets Google.

### AI mode

In AI mode, set it up in Settings → the **การแปล** ("Translation") tab: pick a **ผู้ให้บริการ** ("Provider"), enter the **API key** and **โมเดล** ("Model"), then click **ทดสอบ** ("Test"). If AI is selected but not set up, the app still reads subtitles, but shows them in the original language marked **(แปลไม่ได้)** ("untranslated"), and the status says what is missing (for example **ยังไม่ได้ใส่ API key — ใส่ได้ที่ ตั้งค่า → การแปล**, "no API key yet — enter it in Settings → Translation"). If you set up AI in an earlier version, your provider, key and model are kept.

Each line appears once the AI has translated it; until then the previous line stays on screen. If the AI cannot translate a line, it shows in the original language marked **(แปลไม่ได้)**, and the app retries on its own: after 30 seconds when the AI is down or answers badly, after 60 seconds when rate limited, and only after you change the settings when the key is rejected, the model is not found or the account is out of credit. AI errors show in the tray icon tooltip, in Settings (**สถานะ AI**, "AI status") and in the web page's status banner, and a one-time notification appears when the API key is rejected or the account is out of credit.

Providers (**ผู้ให้บริการ**, "Provider") and where to get a key:

| Provider | Get a key / install | Default model |
|---|---|---|
| Claude | Anthropic Console (console.anthropic.com) | `claude-haiku-5-5` |
| OpenAI | OpenAI Platform (platform.openai.com) | `gpt-5-mini` |
| Gemini | Google AI Studio (aistudio.google.com) | `gemini-3.5-flash-lite` |
| Ollama | Install from ollama.com, then `ollama pull <model>` | none (you pick one) |
| Custom (**กำหนดเอง**) | Any OpenAI-compatible endpoint (enter the URL yourself) | none (enter it yourself) |

- **Cost:** Claude and OpenAI charge per use on your own account. Gemini has a free tier (check Google AI Studio for current limits). Ollama is free and runs locally. Cloud providers bill for every subtitle line translated, so a long session adds up.
- **Model:** the **โมเดล** ("Model") field is editable for every provider. Ollama has no default: click **ดึงรายชื่อโมเดล** ("fetch model list") to pick from the models you have pulled. Subtitles need fast replies, so small or lite/mini/haiku-class models are a better fit.
- **API key:** enter it in the **API key** field, then click **ทดสอบ** ("Test") to translate a sample sentence.
- **Context:** the previous subtitle lines are sent along with their translations. Default 4 lines, adjustable 0–10 under **บริบท** ("Context").
- **Game title (ชื่อเกม (ช่วย AI)):** on the **โปรไฟล์** ("Profiles") tab, set a title in each game's profile so the AI picks better terms and proper names.
- **Ollama on another PC on your LAN:** open **ขั้นสูง** ("Advanced") and change **URL** from `http://localhost:11434/v1` to that machine's address.
- **Key storage:** encrypted with Windows DPAPI for the current Windows user and never logged. If you copy `settings.json` to another PC the key does not come along, and the app shows **ยังไม่ได้ใส่ API key** ("no API key yet"); enter the key again on that PC.

## Translation library

A translation library is a prepared set of translations for one game (currently `gta6`). Subtitles found in the library appear at once without waiting for Google or the AI; every other line is translated by the selected mode as usual.

- **Players:** pick the library in Settings → the **โปรไฟล์** ("Profiles") tab, **คลังคำแปล** ("Translation library") field (per game profile; choose **ไม่ใช้คลัง**, "no library", to turn it off). The app checks for and downloads newer library versions on its own. **เปิดโฟลเดอร์คลังของเกม** ("open the game's library folder") opens that game's library folder. If a zone has **จำคำแปล AI ของโซนนี้ไว้ใช้ซ้ำ** ("remember this zone's AI translations for reuse") ticked, the app keeps the AI's translations of that zone in memory while it runs, so repeated lines are not sent to the AI again. If signs or number plates in the OCR box make lines wait for the translator, tick **แปลเฉพาะบรรทัดที่มีชื่อคนพูด** ("only translate lines with a speaker name"); use it only for games whose subtitles show speaker names.
- **Maintainers:** write `library/<game>/pending.jsonl` (git-ignored) yourself (one `{"en","th","speaker"?,"source"?}` per line, for example translated by hand or with an AI), append it, pack with the tag's version, and commit the updated `manifest.json` and `subtitles.jsonl` **before** pushing the `lib-YYYYMMDD.N` tag (`.github/workflows/library.yml` tests, packs again with the tag's version, which gives the same files, and publishes it as a prerelease):

```
dotnet run --project tools/LibraryTool -- append gta6
dotnet run --project tools/LibraryTool -- pack 20261121.1 dist
dotnet test PSRemoteSub.sln
git add library/gta6/subtitles.jsonl library/gta6/manifest.json
git commit -m "chore(library): gta6 20261121.1"
git tag lib-20261121.1
git push origin main lib-20261121.1
```

Review or edit `pending.jsonl` before `append`, which adds the lines to `subtitles.jsonl` (keeping a `source` you set in the pending file, else `ai`) and deletes `pending.jsonl`. `pack <version> <outDir>` zips every game under `library/` into `<outDir>` (`dist/` is git-ignored) and rewrites each game's `manifest.json` (version, line count and the sha256 of `subtitles.jsonl`). Commit that `manifest.json`, so the library bundled with the next app release matches the latest one.

## Limitations

- **Do not minimize the Remote Play window.** Windows stops delivering frames, so the app stops reading and warns that the Remote Play window is minimized (on the web page, the tray icon and in Settings) until you restore it. Cover it with other windows or move it to another screen instead: covered is fine, minimized is not.
- Google mode uses unofficial endpoints, so it can stop working for a while or be limited, and it does not translate speaker names. In AI mode nothing is translated until a provider is set up. Translations appear a little after the game's subtitle, as fast as the translator answers (small models, or Ollama on a fast enough PC, are quicker).
- OCR quality depends on the game's subtitle font and background.

## Privacy

- **No telemetry.** No usage data is collected or sent.
- Only two things leave your machine: **what is sent for translation** and **the update check, sent to GitHub** (installer builds only). What is sent for translation depends on the mode: in **Google Translate mode** the subtitle text goes to Google Translate (the text only, no context or game title); in **AI mode** it goes only to the AI provider you choose, and not to Google Translate.
- In AI mode the chosen provider receives the subtitle text (including the speaker names read from it, to translate them), the previous lines with their translations, and the game title. Nothing else is sent. Cloud providers charge per use; Ollama on your own PC keeps the data on your machine (pointing at Ollama on another LAN PC sends it to that PC).
- The API key is encrypted with Windows DPAPI and never logged.
- Settings are stored in `%AppData%\PSRemoteSub\settings.json`.
- A diagnostic log is kept in `%AppData%\PSRemoteSub\log.txt` (older entries in `log.1.txt`, at most 1 MB each). It holds only events and errors, **never subtitle text or translations**, and is never sent anywhere. Attach it when you report a bug.

## Build from source

Requires the .NET 10 SDK on Windows.

```
dotnet build PSRemoteSub.sln
dotnet test PSRemoteSub.sln
dotnet run --project src/PSRemoteSub.App -f net10.0-windows10.0.19041.0
dotnet publish src/PSRemoteSub.App -c Release -f net10.0-windows10.0.19041.0 -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

The app targets two frameworks (Windows and macOS), so `dotnet run` on Windows needs `-f net10.0-windows10.0.19041.0`.

On macOS: you need the .NET 10 SDK and Swift (Xcode Command Line Tools); run `tools/mac/run-dev.sh` to build and launch the app in dev mode (not `dotnet run`). `dotnet test PSRemoteSub.sln` does not work on a Mac: the solution includes `tests/PSRemoteSub.Windows.Tests`, which runs only on Windows. Build the solution, then test the other projects one by one:

```
dotnet build PSRemoteSub.sln
dotnet test tests/PSRemoteSub.Core.Tests --no-build
dotnet test tests/PSRemoteSub.Web.Tests --no-build
dotnet test tests/PSRemoteSub.MacOS.Tests --no-build
```

The result is `publish\PSRemoteSub.App.exe`, next to `LICENSE` and `THIRD-PARTY-NOTICES.md`. Builds made by the release workflow get the repository URL injected automatically (`-p:RepositoryUrl=...`), so installed copies can check for updates. Local builds never check, because `<RepositoryUrl>` in `src/PSRemoteSub.App/PSRemoteSub.App.csproj` is empty by default; to test the update check, set it there (for example `https://github.com/OWNER/PSRemoteSub`) or pass `-p:RepositoryUrl=...` to `dotnet publish`.

To cut a release, push a tag such as `v1.0.0`; `.github/workflows/release.yml` builds the Velopack installer and the portable zip. The Windows OCR tests use `[OcrFact]`, which skips them at run time when the English (`en-US`) OCR language pack is missing (as on CI runners) and runs them wherever it is installed, so no test filter is needed.

## Secret scanning

This repo uses [gitleaks](https://github.com/gitleaks/gitleaks) to keep API keys and other secrets out of git.

- **Locally:** install [pre-commit](https://pre-commit.com) (`brew install pre-commit` or `pip install pre-commit`) and run `pre-commit install` once per clone. Every commit is then scanned first. Without the hook, nothing is checked locally.
- **CI:** the `gitleaks` workflow scans the full history on every push and pull request, so it also catches commits made with `--no-verify` or on the web.
- **False positive:** add a `gitleaks:allow` comment at the end of that line, or add the path or value under `[allowlist]` in `.gitleaks.toml`.
- **A finding already in pushed history** (CI red on every branch): `gitleaks:allow` only works in the same commit as the line. Copy the `Fingerprint:` line from the CI log into a `.gitleaksignore` file at the repo root, one per line (rotate the secret first if it is real).
- **A real secret was committed:** rotate it right away. Deleting it in a later commit is not enough, because it stays in history.

## License

MIT. See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
