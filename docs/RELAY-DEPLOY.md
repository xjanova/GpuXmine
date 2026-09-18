# ติดตั้ง relay บนเซิร์ฟเวอร์ xman4289

relay คือตัวที่เครื่องของทุกคนโทรเข้ามาหา เครื่องเป็นฝ่ายเปิดการเชื่อมต่อออกไปเอง
เจ้าของเครื่องจึงไม่ต้องเปิดพอร์ต ไม่ต้องตั้ง firewall และไม่ต้องมี IP จริง —
และนั่นคือเหตุผลเดียวที่ระบบนี้ใช้ได้กับคอมที่บ้านคน

ตอนนี้ยังไม่มี relay สาธารณะ ระบบทั้งเส้นจึงยังเดินไม่ได้บนของจริง
เอกสารนี้คือขั้นตอนที่เหลือทั้งหมด

## ภาพรวมว่าใครคุยกับใคร

```
เครื่องที่บ้าน  ──wss──►  relay.xman4289.com  ◄──https──  aixman (ส่งงาน)
                                  ▲
                                  │ https + X-Admin-Key
                            xmanstudio (ออก worker ตอนจับคู่, ดึงสถานะทุกนาที)
```

`GPUXMINE_RELAY_ADMIN_KEY` เปิดสิทธิ์สร้าง worker ใหม่ได้ทั้งเครือข่าย
**xmanstudio เป็นที่เดียวที่ถือมัน** เจ้าของเครื่องไม่เคยเห็น และ aixman ไม่ต้องรู้จัก relay เลย

## ขั้นตอน

### 1. DNS

ชี้ `relay.xman4289.com` มาที่ไอพีของเซิร์ฟเวอร์ (A record ตัวเดียว)

### 2. ติดตั้งตัวโปรแกรม

```bash
git clone https://github.com/xjanova/GpuXmine.git /tmp/gpuxmine
sudo bash /tmp/gpuxmine/deploy/relay/install.sh
```

สคริปต์จะ:

- ดึงไฟล์ `gpuxmine-relay-linux-x64.zip` จาก release ล่าสุด (self-contained — ไม่ต้องลง .NET)
- สร้างผู้ใช้ระบบ `gpuxmine` และโฟลเดอร์ `/var/lib/gpuxmine-relay`
- สร้าง admin key ใหม่ลง `/etc/gpuxmine-relay.env` (โหมด 600) **แล้วพิมพ์ค่าออกมาให้ครั้งเดียว**
- ติดตั้งและสตาร์ต systemd service ที่ฟังอยู่ `127.0.0.1:5080`

รันซ้ำได้เสมอ การอัปเดตคือการรันสคริปต์นี้อีกครั้ง `workers.json` อยู่คนละที่กับตัวโปรแกรมและไม่ถูกแตะ

### 3. reverse proxy + ใบรับรอง

ก๊อป `deploy/relay/nginx-relay.conf` เข้าไป (บน DirectAdmin ต้องเป็นไฟล์ custom
ที่ DirectAdmin ไม่เขียนทับ) แล้วออกใบรับรองให้ `relay.xman4289.com`

สามอย่างในไฟล์นั้นที่ตัดออกไม่ได้:

| ตั้งค่า | ถ้าไม่มี |
|---|---|
| `Upgrade` / `Connection` headers | nginx ตอบ handshake เป็น 200 ธรรมดา เครื่องต่อไม่ติดสักเครื่อง |
| `X-Forwarded-Proto` / `X-Forwarded-Host` | relay แจก `ws://` กับ `http://` ให้ทุกเครื่อง แล้ว aixman ปฏิเสธ endpoint ที่ไม่ใช่ https — เครื่องจับคู่ได้แต่ไม่เคยได้งาน |
| `proxy_read_timeout 300s` | ค่า default 60 วินาทีตัดงานกลางคัน (วัดจริง: Wan 2.1 ใช้ 185 วิ, SDXL 279 วิ บนการ์ด 8 GB) |

ตรวจว่าใช้ได้:

```bash
curl https://relay.xman4289.com/healthz
# {"ok":true,"online":0,"utc":"..."}
```

### 4. บอก xmanstudio ว่า relay อยู่ไหน

ใส่ใน `.env` ของ production แล้ว `php artisan config:cache`:

```
GPUXMINE_RELAY_URL=https://relay.xman4289.com
GPUXMINE_RELAY_ADMIN_KEY=<ค่าที่สคริปต์พิมพ์ออกมา>
```

จนกว่าจะตั้งสองค่านี้ ปุ่ม "ขอรหัสจับคู่" ที่หน้า `/gpuxmine` จะกดไม่ได้และขึ้นว่าระบบยังไม่พร้อม
— ตั้งใจให้เป็นแบบนั้น ดีกว่าออกรหัสที่แลกอะไรไม่ได้

### 5. ตรวจว่าทั้งเส้นเดินจริง

```bash
# ฝั่งเซิร์ฟเวอร์: ตัวจับเวลาดึงสถานะทุกนาที เรียกมือก็ได้
php artisan gpuxmine:sync-nodes
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
