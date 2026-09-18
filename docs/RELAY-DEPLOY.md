# ติดตั้ง relay บนเซิร์ฟเวอร์ xman4289

relay คือตัวที่เครื่องของทุกคนโทรเข้ามาหา เครื่องเป็นฝ่ายเปิดการเชื่อมต่อออกไปเอง
เจ้าของเครื่องจึงไม่ต้องเปิดพอร์ต ไม่ต้องตั้ง firewall และไม่ต้องมี IP จริง —
และนั่นคือเหตุผลเดียวที่ระบบนี้ใช้ได้กับคอมที่บ้านคน

ตอนนี้ยังไม่มี relay สาธารณะ ระบบทั้งเส้นจึงยังเดินไม่ได้บนของจริง
เอกสารนี้คือขั้นตอนที่เหลือทั้งหมด

## ใครคุยกับใคร

```
เครื่องที่บ้าน  ──wss──►  relay.xman4289.com  ◄──https──  aixman (ส่งงาน)
                                  ▲
                                  │ https + X-Admin-Key
                            xmanstudio (ออก worker ตอนจับคู่, ดึงสถานะทุกนาที)
```

`GPUXMINE_RELAY_ADMIN_KEY` เปิดสิทธิ์สร้าง worker ใหม่ได้ทั้งเครือข่าย
**xmanstudio เป็นที่เดียวที่ถือมัน** เจ้าของเครื่องไม่เคยเห็น และ aixman ไม่ต้องรู้จัก relay เลย

## สิ่งที่รู้อยู่แล้วเกี่ยวกับเครื่องนี้

- **Apache ไม่ใช่ nginx** — nginx ไม่ได้ติดตั้งบนเครื่องนี้ด้วยซ้ำ
- โดเมนอยู่ที่ `/home/admin/domains/<domain>/public_html` (DirectAdmin, user `admin`)
- `ai.xman4289.com` พร็อกซีไป `127.0.0.1:3001` และ `rpc.tpix.online` พร็อกซีไป geth
  ด้วยกลไกเดียวกันนี้อยู่แล้ว — **ใช้แบบเดียวกับที่ใช้ได้อยู่ ไม่ต้องคิดใหม่**
- DNS อยู่ที่ Cloudflare
- แก้ config ต่อโดเมนที่ `/usr/local/directadmin/data/users/admin/domains/<DOMAIN>.conf.CUSTOM.<N>`
  แล้ว `da build rewrite_confs`

## ขั้นตอน

### 0. ตรวจก่อน (อ่านอย่างเดียว)

```bash
bash /tmp/gpuxmine/deploy/relay/preflight.sh
```

บอกว่าเว็บเซิร์ฟเวอร์ตัวไหนรันอยู่ · `mod_proxy_wstunnel` เปิดหรือยัง · และ
**ไฟล์ `.conf.CUSTOM.<N>` ที่โดเมนอื่นใช้พร็อกซีอยู่คือเลขอะไร พร้อมเนื้อในของมัน**

เลขนั้นสำคัญ: เดาผิดแล้ว vhost พัง = ทุกเว็บบนเครื่องล่มพร้อมกัน
ดูของจริงที่ใช้ได้อยู่แล้วดีกว่าเดา

### 1. DNS — ต้องเป็น **grey cloud (DNS only)**

ชี้ `relay.xman4289.com` → ไอพีเซิร์ฟเวอร์ แล้ว**ปิด proxy ของ Cloudflare สำหรับเรคคอร์ดนี้**

ไม่ใช่เรื่องความชอบ: Cloudflare ตัด request ที่ origin ตอบช้ากว่า 100 วินาทีด้วย error 524
งานที่วิ่งผ่านอุโมงค์นี้บางชิ้นยาวกว่านั้น (วัดจริงบนการ์ด 8 GB: Wan 2.1 = 185 วินาที,
SDXL = 279 วินาที) และการดึงไฟล์วิดีโอกลับผ่านเน็ตบ้านก็กินเวลาได้เกินร้อยวินาทีเหมือนกัน
relay เป็นช่องทางของโปรแกรมเราเอง ไม่ใช่หน้าเว็บที่ต้องการแคชหรือกัน DDoS

### 2. ติดตั้งตัวโปรแกรม

```bash
git clone https://github.com/xjanova/GpuXmine.git /tmp/gpuxmine
sudo bash /tmp/gpuxmine/deploy/relay/install.sh
```

สคริปต์จะ:

- ดึง `gpuxmine-relay-linux-x64.zip` จาก release ล่าสุด (self-contained — ไม่ต้องลง .NET)
- สร้างผู้ใช้ระบบ `gpuxmine` และโฟลเดอร์ `/var/lib/gpuxmine-relay`
- สร้าง admin key ลง `/etc/gpuxmine-relay.env` (โหมด 600) **แล้วพิมพ์ค่าออกมาให้ครั้งเดียว**
- ติดตั้งและสตาร์ต systemd service ฟังที่ `127.0.0.1:5080` เท่านั้น

รันซ้ำได้เสมอ การอัปเดตคือการรันสคริปต์นี้อีกครั้ง `workers.json` อยู่คนละที่กับตัวโปรแกรม
และไม่ถูกแตะ — ไฟล์นั้นคือ token hash ของทุกเครื่องในเครือข่าย ถ้าหายคือทุกเครื่องถูก
บอกว่า token ใช้ไม่ได้พร้อมกัน

### 3. Apache reverse proxy

ต้องมีโมดูลครบก่อน (preflight บอกให้):

```bash
sudo da build set_service mod_proxy_wstunnel yes 2>/dev/null || true
httpd -M | grep -E 'proxy_wstunnel|headers|rewrite'
```

แล้วเอาเนื้อจาก `deploy/relay/apache-relay.conf` ใส่ในไฟล์ CUSTOM ฝั่ง SSL
ของโดเมนนี้ (เลขเดียวกับที่ preflight เจอ) แล้ว:

```bash
sudo da build rewrite_confs
sudo systemctl reload httpd
```

สามอย่างในไฟล์นั้นที่ตัดออกไม่ได้:

| ตั้งค่า | ถ้าไม่มี |
|---|---|
| `RewriteCond %{HTTP:Upgrade} =websocket` + `[P]` | Apache ตอบ handshake เป็น HTTP ธรรมดา ไม่มีเครื่องไหนต่อติดเลย |
| `RequestHeader set X-Forwarded-Proto "https"` | Apache **ไม่ใส่ให้เอง** → relay แจก `ws://` และ endpoint `http://` → aixman ปฏิเสธ → เครื่องลงทะเบียนได้แต่ไม่เคยได้งาน |
| `ProxyTimeout 300` | ค่า default 60 วินาทีตัดงานกลางคัน |

ตรวจว่าใช้ได้:

```bash
curl https://relay.xman4289.com/healthz
# {"ok":true,"online":0,"utc":"..."}
```

### 4. บอก xmanstudio ว่า relay อยู่ไหน

ใน `/home/admin/domains/xman4289.com/.env` แล้ว `php artisan config:cache`:

```
GPUXMINE_RELAY_URL=https://relay.xman4289.com
GPUXMINE_RELAY_ADMIN_KEY=<ค่าที่ install.sh พิมพ์ออกมา>
```

จนกว่าจะตั้งสองค่านี้ ปุ่ม "ขอรหัสจับคู่" ที่หน้า `/gpuxmine` จะกดไม่ได้และขึ้นว่าระบบยังไม่พร้อม
— ตั้งใจให้เป็นแบบนั้น ดีกว่าออกรหัสที่แลกอะไรไม่ได้

### 5. ตรวจว่าทั้งเส้นเดินจริง

```bash
php artisan gpuxmine:sync-nodes    # ตัวจับเวลาทำให้ทุกนาทีอยู่แล้ว เรียกมือก็ได้
```

แล้วบนเครื่องที่มีการ์ดจอ:

1. ลงโปรแกรมจาก https://github.com/xjanova/GpuXmine/releases/latest
2. เปิดเว็บ → เครื่องของฉัน → ขอรหัสจับคู่
3. พิมพ์รหัสในโปรแกรมหน้า Settings (หรือ `gpuxmine-agent --pair <รหัส>` ถ้าไม่มีจอ)
4. โปรแกรมรีสตาร์ตเอง ต่อ relay แล้ววัดความเร็วการ์ดตัวเอง
5. หน้า `/gpuxmine` ขึ้นการ์ดจอ · VRAM · คะแนน · งานที่รับได้
6. หลังบ้าน aixman แท็บ "เครื่องชุมชน" เห็นเครื่องเดียวกัน

## ที่ต้องรู้ไว้

**ยังไม่มีโมเดลสำหรับการ์ดบ้าน** โมเดลทุกตัวในแคตตาล็อก aixman ตอนนี้ต้องการ 16–24 GB
การ์ด 8 GB ที่ใช้พัฒนาระบบนี้รันวิดีโอ Wan 2.1 1.3B ได้จริงใน 185 วินาที
แต่ไม่ตรงกับโมเดลไหนเลยที่เราจ่ายงาน หน้าแอดมินจะขึ้นว่า *"การ์ดมี VRAM 8.0 GB ·
โมเดลที่กินน้อยที่สุดในระบบต้องการ 16 GB"* — เครื่องเข้าระบบได้ครบทุกขั้นแต่ยังไม่ได้งาน
จนกว่าจะมีแคตตาล็อกชั้นเล็ก

**token เดียวใช้ได้สองทาง** ทั้งฝั่งเครื่องที่ใช้ต่อเข้ามา และฝั่ง aixman ที่ใช้เรียกผ่านอุโมงค์
ใครถือ token ของ worker ตัวไหนก็แกล้งเป็น worker ตัวนั้นได้ ตอนนี้เก็บเข้ารหัสทั้งสองที่
(`gpu_nodes.relay_token` ด้วย APP_KEY, `ai_gpu_workers.auth_token` ด้วยคีย์ของ aixman)
แยกเป็นคนละใบเมื่อไรก็ได้ที่คิดว่าคุ้ม

**ถอยกลับเวอร์ชันเก่า** สคริปต์เก็บ release ก่อนหน้าไว้หนึ่งรุ่นเสมอ:

```bash
ls /opt/gpuxmine-relay/releases
sudo ln -sfn /opt/gpuxmine-relay/releases/<รุ่นเก่า> /opt/gpuxmine-relay/current
sudo systemctl restart gpuxmine-relay
```

**เครื่องนี้ใช้ nginx ไม่ได้** ไฟล์ `deploy/relay/nginx-relay.conf` เก็บไว้สำหรับเซิร์ฟเวอร์อื่น
ที่ใช้ nginx เท่านั้น บน xman4289 ให้ใช้ `apache-relay.conf`
