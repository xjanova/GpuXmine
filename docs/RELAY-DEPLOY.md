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
**xmanstudio เป็นที่เดียวที่ถือมัน** เจ้าของเครื่องไม่เคยเห็น

aixman **ต้อง**อ่านรายชื่อเครื่องจาก `GET /admin/workers` (ไม่มี key = ไม่เคย probe เครื่องไหนเลย)
แต่ไม่ต้องได้ admin key — ให้ถือ **observer key** (`GPUXMINE_OBSERVER_KEY`) ซึ่งเปิดได้แค่
`GET /admin/workers` อย่างเดียว ส่งมาใน header `X-Admin-Key` เดิมก็ได้ ไม่ต้องแก้โค้ด aixman

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
| `client_max_body_size 32m` | เท่ากับ `Relay__MaxRequestBodyBytes` — ดูข้อ 4.1 |

**`sudo nginx -t` ก่อน reload ทุกครั้ง** — ไฟล์เสียที่ค้างไว้จะทำให้ reload ครั้งถัดไป
รวมถึงตอน certbot ต่ออายุอัตโนมัติ ล้มทั้งเครื่อง

### 4.1 เพดานของ reverse proxy ต้องตรงกับของ relay

`client_max_body_size 32m` (nginx) / `LimitRequestBody 33554432` (Apache) = `Relay__MaxRequestBodyBytes`
สองชั้นต้องปฏิเสธสิ่งเดียวกัน ค่านี้คุมเฉพาะ **ขนาด request** (ไฟล์ที่ aixman อัปโหลดไปให้เครื่อง)
ไม่เกี่ยวกับผลงานที่ไหลกลับ — ผลงานไหลผ่านเป็นก้อน ๆ ไม่มีเพดานรวมที่ proxy

### 5. บอก xmanstudio ว่า relay อยู่ไหน

ใน `/home/admin/domains/xman4289.com/.env` บนเครื่องเว็บ:

```
GPUXMINE_RELAY_URL=https://relay.xman4289.com:8443
GPUXMINE_RELAY_ADMIN_KEY=<ค่าจาก /etc/gpuxmine-relay.env บนเครื่อง TPIX>
```

แล้ว `php artisan config:cache`

จนกว่าจะตั้งสองค่านี้ ปุ่ม "ขอรหัสจับคู่" ที่หน้า `/gpuxmine` จะกดไม่ได้และขึ้นว่าระบบยังไม่พร้อม
— ตั้งใจให้เป็นแบบนั้น ดีกว่าออกรหัสที่แลกอะไรไม่ได้

ฝั่ง aixman: `GPUXMINE_RELAY_URL=https://relay.xman4289.com:8443` (ต้องตรงกับที่ xmanstudio ใช้
ทุกตัวอักษร รวม `:8443`) และ vendor key ของ gpuxmine ในหลังบ้าน (Admin → GPU) = **observer key**
ไม่ใช่ admin key — `install.sh` สร้างและพิมพ์ให้ครั้งเดียวตอนติดตั้ง ดูย้อนหลังได้ใน `/etc/gpuxmine-relay.env`

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

## token สองใบ: ของเครื่อง กับของ aixman

เดิม token ใบเดียวเปิดได้ทั้ง `/agent` (เครื่องต่อเข้ามา) และ `/w/` (aixman ส่งงาน)
ใครได้ `agent.json` ของเครื่องไหนไปก็เป็น aixman ได้ ใครได้ฐานข้อมูล aixman ไปก็เป็นเครื่องได้

ตอนนี้ `/enroll` ตอบ `token` (ให้เครื่อง ใช้กับ `/agent` เท่านั้น) และ `tunnelToken` (ให้ aixman ใช้กับ `/w/` เท่านั้น)
worker ที่มี tunnel token แล้ว **token ของเครื่องใช้กับ `/w/` ไม่ได้อีก** ส่วน worker รุ่นก่อนแยก (ไม่มี tunnel token)
ยังใช้ใบเดิมได้ทั้งสองทางเหมือนเดิม

**ลำดับ deploy** — การแยกปิดไว้ก่อน (`Relay__IssueTunnelTokens` ค่า default = false) เพราะ xmanstudio
รุ่นก่อนหน้านี้ส่ง `token` ของเครื่องให้ aixman ถ้า relay เริ่มแยกก่อน เครื่องที่จับคู่ในช่วงนั้นจะลงทะเบียนได้
แต่ aixman เรียกไม่ติดเลย ตอนปิดอยู่ `/enroll` ยังตอบ `tunnelToken` แต่เป็นค่าเดียวกับ `token`

1. deploy relay รุ่นนี้ (ก่อนหรือหลังรีโปอื่นก็ได้ ไม่มีอะไรพัง)
2. deploy xmanstudio รุ่นที่เก็บ `tunnel_token` และส่ง `tunnel_token ?? relay_token` ให้ aixman
3. เปิด: เพิ่ม `Relay__IssueTunnelTokens=true` ใน `/etc/gpuxmine-relay.env` แล้ว `systemctl restart gpuxmine-relay`
4. ย้าย worker เก่าทีละตัวโดยไม่ต้องให้เจ้าของจับคู่ใหม่: `POST /admin/workers/{id}/rotate?only=tunnel`
   ออก tunnel token ใหม่ให้อย่างเดียว เครื่องยังต่ออยู่ตามเดิม — xmanstudio ต้องเก็บค่าที่ได้และส่งให้ aixman ทันที
   (ระหว่างนั้น aixman จะได้ 401 จาก token เก่า)

## คำสั่งจัดการ worker

ทุกคำสั่งใช้ `X-Admin-Key` ยกเว้นบรรทัดแรกที่ observer key ก็พอ

| คำสั่ง | ผล |
|---|---|
| `GET /admin/workers` | รายชื่อทั้งหมด + `online` `busy` `accepting` `lastSeenAt` `disabled` `hasTunnelToken` · `busy`/`accepting` มาจาก heartbeat ของเครื่องตรง ๆ เป็น `null` เมื่อไม่รู้ (ออฟไลน์ หรือเครื่องรุ่นเก่าไม่ได้ส่งมา) ห้ามอ่านเป็น "ว่าง" |
| `POST /enroll?label=` | worker ใหม่ → `{workerId, token, tunnelToken, agentRelayUrl, aixmanEndpoint}` |
| `POST /admin/workers/{id}/disable?reason=` | ตัดทั้งสองทาง socket ที่ต่ออยู่ถูกปิดทันที เครื่องได้ **403** (ไม่ใช่ 401) เมื่อต่อใหม่ ประวัติยังอยู่ |
| `POST /admin/workers/{id}/enable` | กลับมาใช้ได้ด้วย token เดิม |
| `POST /admin/workers/{id}/rotate` | ออก token ใหม่ทั้งสองใบ ใบเก่าใช้ไม่ได้ทันที socket ถูกปิด — เครื่องต้องจับคู่ใหม่ |
| `POST /admin/workers/{id}/rotate?only=tunnel` | ออกเฉพาะ tunnel token เครื่องไม่หลุด (`token` ในคำตอบเป็น `null`) |
| `DELETE /admin/workers/{id}` | ลบถาวร socket ถูกปิด · เรียกซ้ำได้ ตอบ 200 `{deleted:true, existed:false}` ถ้าไม่มีอยู่แล้ว |

worker ที่ไม่รู้จัก: disable / enable / rotate ตอบ 404 `{error:"unknown-worker"}`

**401 กับ 403 ต่างกัน** — 401 = ไม่รู้จัก worker หรือ token ผิด (ต้องจับคู่ใหม่) · 403 = ถูกปิดโดยแอดมิน
(รอเปิด) เครื่องรุ่นใหม่ถอยไปลองใหม่ทุก 5 นาทีเมื่อเจอสองอย่างนี้ แทนที่จะเคาะถี่ ๆ

## อุโมงค์ส่งต่อแค่สิ่งที่ aixman ใช้

`/w/{id}/*` ปฏิเสธทุก path ที่ไม่อยู่ในรายการนี้ด้วย 403 `{error:"path-not-allowed"}` — ป้องกัน
ComfyUI-Manager (ติดตั้ง custom node = รันโค้ดบนเครื่องเจ้าของ), `/userdata`, `/free` ฯลฯ
แม้เครื่องจะเป็นรุ่นเก่าที่ยังไม่มีรายการนี้เอง

`GET /object_info[/{cls}]` · `POST /prompt` · `GET /history/{id}` · `GET /history?max_items=` ·
`POST /history` (เฉพาะ `{"delete":[...]}` / `{"clear":false}`) · `GET /queue` · `GET /view` (ชื่อไฟล์ห้ามมี `..` `:` หรือ backslash
และ type ต้องเป็น output/input/temp) · `POST /upload/image` · `POST /interrupt` · `GET|POST /aixman/*`

รายการเดียวกันอยู่ใน `src/GpuxMine.Protocol/TunnelAllowlist.cs` ตัวเครื่องใช้ชุดเดียวกัน จะได้ไม่มีวันไม่ตรงกัน

## ผลงานไหลกลับเป็นก้อน ไม่ใช่ทั้งไฟล์

relay รุ่นก่อนรอจนเครื่องส่งคำตอบมาครบทั้งไฟล์ในเฟรมเดียว (สูงสุด 192 MB) เก็บไว้ในหน่วยความจำ
แล้วค่อยส่งต่อ ภายในเวลารวม 180 วินาที — วิดีโอจากเน็ตบ้านช้า ๆ จึงล้มหลังเรนเดอร์เสร็จแล้ว
และเครื่องที่ตั้งใจส่งเฟรมใหญ่ไม่กี่ตัวทำให้ relay ถูกฆ่าเพราะ RAM เต็ม — ทุกเครื่องหลุดพร้อมกัน

ตอนนี้:

- relay อ่าน header ของเฟรมก่อน แล้วส่ง body ต่อให้ aixman **ทันทีที่มาถึง** ไม่ประกอบเป็นก้อนใหญ่
  เฟรมของ request ที่ไม่มีใครรอแล้ว (aixman ตัดสาย / หมดเวลา) ถูกทิ้งตั้งแต่ไบต์แรก
- 180 วินาทีกลายเป็น **เวลาเงียบ** — นับใหม่ทุกครั้งที่มีข้อมูลมาถึง ไฟล์ใหญ่ที่ไหลอยู่ไม่ถูกตัด
- เครื่องที่ส่งเร็วกว่า aixman อ่าน ถูกเก็บไว้ได้แค่ในงบ (`SessionBufferBytes` ต่อเครื่อง, ครึ่งหนึ่งต่อคำตอบ,
  `BufferBudgetBytes` ทั้ง relay) เกินงบแล้วรอ `StallSeconds` ยังไม่มีที่ = ตัดคำตอบนั้นคำตอบเดียว
- คำตอบที่ถูกตัดกลางทาง relay **ตัดการเชื่อมต่อ** กับ aixman ไม่จบ response แบบปกติ —
  ครึ่งรูปต้องไม่ถูกอ่านเป็นรูปเต็ม
- เมื่อ aixman ตัดสาย relay บอกเครื่องให้หยุดทำ request นั้น (`cancel`) ไม่ใช่ปิดทั้ง socket

**ใช้ได้ทุกคู่รุ่น** — relay รุ่นนี้บอกใน `hello-ack` ว่ารับคำตอบแบบก้อน (`caps: ["res-stream"]`)
เครื่องรุ่นใหม่ส่งแบบก้อน (`res-head` / `res-chunk` ไม่เกิน 1 MB / `res-end`) **เฉพาะเมื่อเห็นคำนี้**:

| | relay เก่า (0.1.x) | relay รุ่นนี้ |
|---|---|---|
| เครื่องเก่า (0.1.x) | เหมือนเดิม | ส่งเฟรมเดียวเหมือนเดิม relay ไหลต่อให้เป็นก้อนเอง |
| เครื่องรุ่นใหม่ | ส่งเฟรมเดียว (ไฟล์เกินเพดาน 192 MB → ตอบ 413 แทน ไม่ทำให้ session หลุด) | ส่งเป็นก้อน |

`Relay__StreamReplies=false` ทำให้ relay ทำตัวเหมือนรุ่นเก่าในสายตาเครื่อง ไว้ซ้อมถอยกลับ

## เพดานและการตั้งค่า

ตั้งผ่าน environment (`Relay__ชื่อ`) ใน unit หรือ `/etc/gpuxmine-relay.env` — ค่า default คือค่าที่ใช้จริง

| ค่า | default | คุมอะไร |
|---|---|---|
| `TunnelTimeoutSeconds` | 180 | เวลาเงียบสูงสุดของหนึ่ง request (ไม่ใช่เวลารวม) |
| `MaxRequestBodyBytes` | 32 MiB | body ของ request ทุกเส้นทาง ตั้งให้ Kestrel ตรง ๆ ด้วย (เดิมใช้ค่าแฝง 30,000,000 ของ Kestrel) |
| `MaxReplyBytes` | 1 GiB | คำตอบหนึ่งคำตอบ เกินแล้วถูกตัด |
| `SessionBufferBytes` | 16 MiB | ไบต์ที่ถือไว้ให้เครื่องหนึ่งเครื่อง (คำตอบเดียวได้ครึ่งเดียว — health probe ไม่ต้องรอหลังไฟล์ใหญ่) |
| `BufferBudgetBytes` | 384 MiB | ทั้ง relay รวม request ที่รอส่งให้เครื่อง — ต่ำกว่า `MemoryHigh` โดยตั้งใจ |
| `StallSeconds` | 10 | รอที่ว่างในงบได้นานเท่านี้ ก่อนตัดคำตอบนั้น |
| `StaleAfterSeconds` | 90 | session ที่ไม่ส่งอะไรมาเลย (แม้แต่ heartbeat) นานเท่านี้ถูกปิด — โน้ตบุ๊กที่พับจอ TCP เองต้องใช้ ~15 นาทีกว่าจะรู้ |
| `KeepAliveTimeoutSeconds` | 0 (ปิด) | ping-pong ของ WebSocket — ปิดไว้เพราะเครื่องรุ่นเก่าตอบ ping ไม่ได้ระหว่างส่งไฟล์ใหญ่ทั้งก้อน `StaleAfterSeconds` นับไบต์แทน ซึ่งเครื่องที่ทำงานอยู่ส่งมาเสมอ |
| `StreamReplies` | true | บอกเครื่องว่ารับคำตอบแบบก้อน |
| `IssueTunnelTokens` | false | ดู "token สองใบ" ข้างบน |
| `AgentConnectsPerMinutePerIp` | 60 | `/agent` ต่อ IP |
| `AgentConnectsPerMinutePerWorker` | 12 | `/agent` ต่อ worker — สองเครื่องที่ใช้ตัวตนเดียวกันจะเตะกันไปมา ค่านี้กันไม่ให้กลายเป็นพายุ |
| `TunnelConcurrencyPerWorker` | 16 | request ที่ค้างพร้อมกันต่อ worker |
| `TunnelRequestsPerMinutePerWorker` | 600 | request ต่อนาทีต่อ worker |
| `AdminRequestsPerMinutePerIp` | 1200 | `/enroll` `/admin/*` และ `/w/` ของ worker ที่ไม่รู้จัก ต่อ IP |

เกินเพดาน rate = 429 `{error:"rate-limited"}` พร้อม `Retry-After`

## ทะเบียน worker: สำรองและกู้คืน

`workers.json` คือ token hash ของทุกเครื่อง — หาย = ทุกเครื่องถูกบอกว่า token ใช้ไม่ได้พร้อมกัน

- ทุกครั้งที่เขียน relay เก็บไฟล์ก่อนหน้าเป็น `workers.json.bak` และสำเนาแรกของแต่ละวันใน
  `backups/workers-YYYYMMDD.json` (เก็บ 14 วัน) — เขียนลงดิสก์ก่อน rename เสมอ
- `install.sh` เก็บ `backups/preinstall-workers-<เวลา>.json` ทุกครั้งก่อนเปลี่ยนตัวโปรแกรม (เก็บ 5 ชุด)
- ถ้า `workers.json` อ่านไม่ได้ relay **ไม่ยอมเริ่ม** และบอกใน log ว่าสำเนาอยู่ไหน
- ถ้า `workers.json` หายไปแต่ `.bak` ยังอยู่ relay เริ่มแบบว่างพร้อม log ระดับ error — **ไม่กู้เอง**
  เพราะสำเนาช้ากว่าหนึ่งครั้ง และครั้งนั้นอาจเป็นการลบหรือปิด worker ที่ต้องปิดต่อไป

กู้คืน (เลือกไฟล์ที่ใหม่ที่สุดที่ไว้ใจได้ แล้วเช็คว่า worker ที่เพิ่งลบ/ปิดไม่กลับมา):

```bash
sudo systemctl stop gpuxmine-relay
sudo cp -p /var/lib/gpuxmine-relay/workers.json.bak /var/lib/gpuxmine-relay/workers.json
sudo chown gpuxmine:gpuxmine /var/lib/gpuxmine-relay/workers.json
sudo systemctl start gpuxmine-relay
```

ไฟล์ที่ relay รุ่นนี้เขียนยังอ่านได้ด้วยรุ่นก่อน (ชื่อฟิลด์ `TokenHash` เดิม) ถอยกลับไม่ทำให้เครื่องหลุด
แต่รุ่นก่อนไม่รู้จัก tunnel token และการปิด worker — ถอยแล้ว aixman ต้องกลับไปใช้ token ของเครื่อง

## ของจริงอยู่ที่ไหนบ้าง (เครื่อง TPIX)

| | ที่อยู่ |
|---|---|
| service | `/etc/systemd/system/gpuxmine-relay.service` · `Type=exec` · CPUQuota 400% · MemoryMax 1G · IOWeight 50 · NOFILE 65535 |
| ไบนารี | `/opt/gpuxmine-relay/current` → `releases/<timestamp>` (เก็บย้อนหลังหนึ่งรุ่น) |
| ทะเบียน worker | `/var/lib/gpuxmine-relay/workers.json` · สำรอง `workers.json.bak` + `backups/` |
| admin key · observer key | `/etc/gpuxmine-relay.env` (โหมด 600) |
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

**token สองใบยังไม่เปิดใช้** โค้ดแยกแล้ว (ดู "token สองใบ") แต่ต้องรอ xmanstudio รุ่นที่เก็บ `tunnel_token`
แล้วค่อยตั้ง `Relay__IssueTunnelTokens=true` และย้าย worker เก่าด้วย `rotate?only=tunnel`

**แบนด์วิดท์** เครื่อง TPIX ใช้อยู่ ~0.2 GB/วันก่อนมี relay งานวิดีโอเป็นคนละระดับ
ถ้าลิงก์คิดค่าทราฟฟิกหรือมีโควตา ต้องเฝ้าดู

---

## ภาคผนวก: ถ้าจะย้ายไปเครื่อง Apache/DirectAdmin

`deploy/relay/apache-relay.conf` + `deploy/relay/preflight.sh` เตรียมไว้แล้ว
`preflight.sh` อ่านอย่างเดียว บอกว่าเว็บเซิร์ฟเวอร์ตัวไหนรันอยู่ · `mod_proxy_wstunnel`
เปิดหรือยัง · และไฟล์ `.conf.CUSTOM.<N>` ที่โดเมนอื่นใช้พร็อกซีอยู่คือเลขอะไร พร้อมเนื้อในของมัน

บน Apache ต้องมี `mod_proxy_wstunnel` และต้องใส่ `RequestHeader set X-Forwarded-Proto "https"`
เอง เพราะ Apache ใส่ `X-Forwarded-For` กับ `X-Forwarded-Host` ให้ แต่ proto ไม่ใส่

`deploy/relay/nginx-relay.conf` คือบล็อก 8443 ตามที่บันทึกไว้ข้างบน (`$http_host`, ไม่มี `http2 on;`)
