# ติดตั้ง relay

> **ติดตั้งจริงแล้วที่ `https://relay.xman4289.com:8443`** (2026-09-18)
> อยู่บนเซิร์ฟเวอร์ TPIX `123.253.62.252` ไม่ใช่เครื่องเว็บ
> เอกสารนี้คือสิ่งที่ทำไปจริงและเหตุผลของแต่ละข้อ

relay คือตัวที่เครื่องของทุกคนโทรเข้ามาหา เครื่องเป็นฝ่ายเปิดการเชื่อมต่อออกไปเอง
เจ้าของเครื่องจึงไม่ต้องเปิดพอร์ต ไม่ต้องตั้ง firewall และไม่ต้องมี IP จริง —
และนั่นคือเหตุผลเดียวที่ระบบนี้ใช้ได้กับคอมที่บ้านคน

```
เครื่องที่บ้าน  ──wss──►  relay.xman4289.com:8443  ◄──https──  aixman (ส่งงาน)
                                    ▲
                                    │ https + X-Admin-Key
                              xmanstudio (ออก worker ตอนจับคู่, ดึงสถานะทุกนาที)
```

`GPUXMINE_RELAY_ADMIN_KEY` เปิดสิทธิ์สร้าง worker ใหม่ได้ทั้งเครือข่าย
**xmanstudio เป็นที่เดียวที่ถือมัน** เจ้าของเครื่องไม่เคยเห็น และ aixman ไม่ต้องรู้จัก relay เลย

## ทำไมอยู่บนเครื่อง TPIX ไม่ใช่เครื่องเว็บ

เครื่องเว็บ (xman4289, `123.253.62.251`) ใช้ **Apache ผ่าน DirectAdmin** — nginx ไม่ได้ติดตั้งด้วยซ้ำ
การแทรก config ต้องเดาเลข `.conf.CUSTOM.<N>` ในเทมเพลต vhost และเดาผิดทีเดียว
คือทุกเว็บบนเครื่องล่มพร้อมกัน

เครื่อง TPIX มี nginx ตรง ๆ (เพิ่มไฟล์ใน `sites-enabled/` จบ) และว่างกว่ามาก —
16 คอร์ใช้อยู่ ~18%, RAM เหลือ 5.5 GB ส่วน relay ที่รันจริงกิน RAM แค่ **25 MB**

ข้อแลกคือมันเป็นเครื่องเดียวกับ validator ของบล็อกเชน จึงกันไม่ให้รบกวนกันด้วยสองอย่าง:

**พอร์ตแยก** — relay อยู่ที่ 8443 ไม่ใช่ 443 เพราะ UFW ของเครื่องนั้นเปิด 80/443
ให้เฉพาะ IP ของ Cloudflare เพื่อกันยิงตรง origin (ด่านที่สร้างไว้หลังเคสถล่ม mempool)
ถ้าเอา relay ไปแปะบน 443 ต้องเปิดให้คนทั้งโลก = ด่านนั้นหายไปทันที

**เพดานทรัพยากรใน systemd** — `CPUQuota=400%` (4 จาก 16 คอร์) · `MemoryMax=1G` ·
`IOWeight=50` (ต่ำกว่า default ถ้าดิสก์แย่งกัน validator ได้ไปก่อน) · `Nice=5`
ไม่ใช่การจูน แต่เป็นสัญญาว่าต่อให้ relay ถูกยิงถล่มหรือรั่ว มันแตะได้ไม่เกินนี้

## ขั้นตอนที่ทำไป

### 1. DNS — **grey cloud (DNS only)**

`relay.xman4289.com` → ไอพีเซิร์ฟเวอร์ โดย**ปิด proxy ของ Cloudflare**

ไม่ใช่เรื่องความชอบ: Cloudflare ตัด request ที่ origin ตอบช้ากว่า 100 วินาทีด้วย error 524
การดึงไฟล์วิดีโอกลับผ่านเน็ตบ้านกินเวลาเกินร้อยวินาทีได้ง่าย ๆ และพอร์ต 8443
Cloudflare proxy ก็ไม่รับอยู่แล้ว

### 2. ตัวโปรแกรม

```bash
git clone https://github.com/xjanova/GpuXmine.git /tmp/gpuxmine
sudo bash /tmp/gpuxmine/deploy/relay/install.sh
```

ดึง `gpuxmine-relay-linux-x64.zip` จาก release ล่าสุด (self-contained ไม่ต้องลง .NET)
สร้างผู้ใช้ `gpuxmine` ตั้ง systemd ฟังที่ `127.0.0.1:5080` และ**พิมพ์ admin key ออกมาครั้งเดียว**

รันซ้ำได้เสมอ — การอัปเดตคือรันสคริปต์นี้อีกครั้ง `workers.json` อยู่คนละที่กับตัวโปรแกรม
และไม่ถูกแตะ (ไฟล์นั้นคือ token hash ของทุกเครื่องในเครือข่าย ถ้าหาย = ทุกเครื่องถูกบอกว่า
token ใช้ไม่ได้พร้อมกัน)

### 3. ใบรับรอง

พอร์ต 80 ถูกเปิดให้คนทั้งโลก (จากเดิม Cloudflare เท่านั้น) เพื่อให้ ACME เข้าถึงได้ —
บนพอร์ตนั้นมีแต่ redirect กับ `/.well-known/acme-challenge/` ไม่มีเนื้อหาอะไรหลุด
และ 443 ยังคงเป็น Cloudflare-only เหมือนเดิม

```bash
sudo apt-get install -y certbot
sudo certbot certonly --webroot -w /var/www/html -d relay.xman4289.com
```

`certbot.timer` ต่ออายุให้เองตลอด ไม่ต้องใช้ Cloudflare API token

### 4. nginx

`/etc/nginx/sites-available/gpuxmine-relay.conf` — แยกไฟล์จาก `tpix-*.conf` โดยตั้งใจ
แก้พังก็พังแค่ relay สองบล็อก: พอร์ต 80 (ACME + redirect) และ 8443 (TLS + proxy)

สี่อย่างที่ตัดออกไม่ได้:

| ตั้งค่า | ถ้าไม่มี |
|---|---|
| `proxy_set_header Upgrade` + `Connection` | nginx ตอบ handshake เป็น 200 ธรรมดา ไม่มีเครื่องไหนต่อติด |
| `proxy_set_header Host $http_host` | `$host` **ตัดพอร์ตทิ้ง** → relay แจก URL ที่ไม่มี `:8443` → ทุกเครื่องไปเคาะ 443 ที่ปิดอยู่ |
| `X-Forwarded-Proto https` | relay แจก `ws://` และ endpoint `http://` → aixman ปฏิเสธ → ลงทะเบียนได้แต่ไม่เคยได้งาน |
| `proxy_read_timeout 300s` | ค่า default 60 วินาทีตัดงานกลางคัน |

**`sudo nginx -t` ก่อน reload ทุกครั้ง** — ไฟล์เสียที่ค้างไว้จะทำให้ reload ครั้งถัดไป
รวมถึงตอน certbot ต่ออายุอัตโนมัติ ล้มทั้งเครื่อง

### 5. บอก xmanstudio ว่า relay อยู่ไหน

ใน `/home/admin/domains/xman4289.com/.env` บนเครื่องเว็บ:

```
GPUXMINE_RELAY_URL=https://relay.xman4289.com:8443
GPUXMINE_RELAY_ADMIN_KEY=<ค่าจาก /etc/gpuxmine-relay.env บนเครื่อง TPIX>
```

แล้ว `php artisan config:cache`

จนกว่าจะตั้งสองค่านี้ ปุ่ม "ขอรหัสจับคู่" ที่หน้า `/gpuxmine` จะกดไม่ได้และขึ้นว่าระบบยังไม่พร้อม
— ตั้งใจให้เป็นแบบนั้น ดีกว่าออกรหัสที่แลกอะไรไม่ได้

### 6. ตรวจว่าทั้งเส้นเดินจริง

1. ลงโปรแกรมจาก https://github.com/xjanova/GpuXmine/releases/latest
2. เปิดเว็บ → เครื่องของฉัน → ขอรหัสจับคู่
3. พิมพ์รหัสในโปรแกรมหน้า Settings (หรือ `gpuxmine-agent --pair <รหัส>` ถ้าไม่มีจอ)
4. โปรแกรมรีสตาร์ตเอง ต่อ relay แล้ววัดความเร็วการ์ดตัวเอง
5. หน้า `/gpuxmine` ขึ้นการ์ดจอ · VRAM · คะแนน · งานที่รับได้
6. หลังบ้าน aixman แท็บ "เครื่องชุมชน" เห็นเครื่องเดียวกัน

## บทเรียนจากการติดตั้งจริง

สี่อย่างนี้พังจริง ทุกข้อจับได้ก่อนถึงมือผู้ใช้ แต่ไม่มีข้อไหนประกาศตัวเอง

**1. `$host` ตัดพอร์ตทิ้ง** relay สร้าง URL ลงทะเบียนจาก Host header เลยแจก
`wss://relay.xman4289.com/agent` (ไม่มี `:8443`) ให้ทุกเครื่อง = ไปเคาะ 443 ที่เปิดเฉพาะ Cloudflare
ต้องใช้ `$http_host` (บน Apache `ProxyPreserveHost On` เก็บพอร์ตให้อยู่แล้ว)

**2. `Type=notify` ค้างที่ activating** ASP.NET Core ส่งสัญญาณ ready ให้ systemd
ก็ต่อเมื่อเรียก `UseSystemd()` ซึ่ง relay ไม่ได้เรียก systemd เลยค้างแล้วจะฆ่าทิ้ง
เมื่อครบ TimeoutStartSec ทั้งที่โปรแกรมตอบ `/healthz` อยู่ → `Type=exec`

**3. `http2 on;` เป็นไวยากรณ์ของ nginx 1.25.1+** เครื่องนี้ 1.24 ต้องเขียน `listen 8443 ssl http2;`

**4. ย้าย relay = worker เดิมใช้ไม่ได้ทั้งหมด** token hash อยู่ใน `workers.json` ของ relay
แต่ละตัว ย้ายเครื่องเมื่อไรรายการเริ่มนับหนึ่งใหม่ xmanstudio จึงถาม relay ก่อนว่ายังรู้จัก
worker นั้นไหม ถ้าไม่รู้จักให้ออก worker ใหม่ทับแถวเดิม (เจ้าของ ชื่อเครื่อง ประวัติยังอยู่)
และ URL ของ relay อ่านจาก config ทุกครั้ง ไม่ใช่เล่นซ้ำจากแถวที่เขียนไว้วันแรก

## ของจริงอยู่ที่ไหนบ้าง (เครื่อง TPIX)

| | ที่อยู่ |
|---|---|
| service | `/etc/systemd/system/gpuxmine-relay.service` · `Type=exec` · CPUQuota 400% · MemoryMax 1G · IOWeight 50 · NOFILE 65535 |
| ไบนารี | `/opt/gpuxmine-relay/current` → `releases/<timestamp>` (เก็บย้อนหลังหนึ่งรุ่น) |
| ทะเบียน worker | `/var/lib/gpuxmine-relay/workers.json` |
| admin key | `/etc/gpuxmine-relay.env` (โหมด 600) |
| nginx | `/etc/nginx/sites-available/gpuxmine-relay.conf` |
| ใบรับรอง | Let's Encrypt `relay.xman4289.com` ต่ออายุผ่าน `certbot.timer` |
| firewall | เปิดเพิ่มแค่ `80/tcp` (ACME) และ `8443/tcp` — 443 ยังเป็น Cloudflare-only |
| log | `journalctl -u gpuxmine-relay -f` · `/var/log/nginx/gpuxmine-relay.access.log` |

**ถอยกลับเวอร์ชันเก่า**

```bash
ls /opt/gpuxmine-relay/releases
sudo ln -sfn /opt/gpuxmine-relay/releases/<รุ่นเก่า> /opt/gpuxmine-relay/current
sudo systemctl restart gpuxmine-relay
```

## ยังเหลือ

**ยังไม่มีโมเดลสำหรับการ์ดบ้าน** โมเดลทุกตัวในแคตตาล็อก aixman ต้องการ 16–24 GB
การ์ด 8 GB รันวิดีโอ Wan 2.1 1.3B ได้จริงใน 185 วินาที แต่ไม่ตรงกับโมเดลไหนเลยที่เราจ่ายงาน
หน้าแอดมินจะขึ้นว่า *"การ์ดมี VRAM 8.0 GB · โมเดลที่กินน้อยที่สุดในระบบต้องการ 16 GB"*
— เครื่องเข้าระบบได้ครบทุกขั้นแต่ยังไม่ได้งานจนกว่าจะมีแคตตาล็อกชั้นเล็ก

**token เดียวใช้ได้สองทาง** ทั้งฝั่งเครื่องที่ต่อเข้ามา และฝั่ง aixman ที่เรียกผ่านอุโมงค์
ใครถือ token ของ worker ตัวไหนก็แกล้งเป็น worker ตัวนั้นได้ ตอนนี้เก็บเข้ารหัสทั้งสองที่
แยกเป็นคนละใบเมื่อไรก็ได้ที่คิดว่าคุ้ม

**แบนด์วิดท์** เครื่อง TPIX ใช้อยู่ ~0.2 GB/วันก่อนมี relay งานวิดีโอเป็นคนละระดับ
ถ้าลิงก์คิดค่าทราฟฟิกหรือมีโควตา ต้องเฝ้าดู

---

## ภาคผนวก: ถ้าจะย้ายไปเครื่อง Apache/DirectAdmin

`deploy/relay/apache-relay.conf` + `deploy/relay/preflight.sh` เตรียมไว้แล้ว
`preflight.sh` อ่านอย่างเดียว บอกว่าเว็บเซิร์ฟเวอร์ตัวไหนรันอยู่ · `mod_proxy_wstunnel`
เปิดหรือยัง · และไฟล์ `.conf.CUSTOM.<N>` ที่โดเมนอื่นใช้พร็อกซีอยู่คือเลขอะไร พร้อมเนื้อในของมัน

บน Apache ต้องมี `mod_proxy_wstunnel` และต้องใส่ `RequestHeader set X-Forwarded-Proto "https"`
เอง เพราะ Apache ใส่ `X-Forwarded-For` กับ `X-Forwarded-Host` ให้ แต่ proto ไม่ใส่

`deploy/relay/nginx-relay.conf` เป็นตัวอย่างสำหรับ nginx บนพอร์ต 443 มาตรฐาน
