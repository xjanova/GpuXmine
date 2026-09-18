#!/usr/bin/env bash
#
# ติดตั้ง / อัปเดต GPUxMINE relay บนเซิร์ฟเวอร์ Linux
#
#   sudo ./install.sh              # เอา release ล่าสุดจาก GitHub
#   sudo ./install.sh v0.1.1       # เจาะจงเวอร์ชัน
#
# รันซ้ำได้เสมอ: อัปเดตคือการรันสคริปต์นี้อีกครั้ง ตัวเก่าถูกเก็บไว้หนึ่งรุ่น
# เผื่อต้องถอยกลับ ส่วน workers.json อยู่คนละที่กับตัวโปรแกรมและไม่ถูกแตะ
# — ไฟล์นั้นคือ token hash ของทุกเครื่องในเครือข่าย ถ้าหายคือทุกเครื่อง
# ถูกบอกว่า token ใช้ไม่ได้พร้อมกัน
set -euo pipefail

REPO="xjanova/GpuXmine"
ROOT="/opt/gpuxmine-relay"
STATE="/var/lib/gpuxmine-relay"
ENV_FILE="/etc/gpuxmine-relay.env"
SERVICE="gpuxmine-relay"
VERSION="${1:-latest}"

if [[ $EUID -ne 0 ]]; then
    echo "ต้องรันด้วย sudo" >&2
    exit 1
fi

for tool in curl tar systemctl; do
    command -v "$tool" >/dev/null || { echo "ไม่มีคำสั่ง $tool" >&2; exit 1; }
done

echo "==> หา release ($VERSION)"
if [[ "$VERSION" == "latest" ]]; then
    API="https://api.github.com/repos/$REPO/releases/latest"
else
    API="https://api.github.com/repos/$REPO/releases/tags/$VERSION"
fi

# grep แทน jq เพราะเซิร์ฟเวอร์ DirectAdmin ส่วนใหญ่ไม่มี jq ติดมา และการ
# ติดตั้งแพ็กเกจเพิ่มบนเครื่องที่รันเว็บจริงไม่ใช่สิ่งที่สคริปต์ติดตั้งควรทำเอง
ASSET_URL="$(curl -fsSL "$API" \
    | grep -o '"browser_download_url": *"[^"]*gpuxmine-relay-linux-x64\.zip"' \
    | head -1 | cut -d'"' -f4)"

if [[ -z "$ASSET_URL" ]]; then
    echo "ไม่พบไฟล์ gpuxmine-relay-linux-x64.zip ใน release นี้" >&2
    exit 1
fi
echo "    $ASSET_URL"

echo "==> เตรียมผู้ใช้และโฟลเดอร์"
id -u gpuxmine >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin gpuxmine
mkdir -p "$ROOT/releases" "$STATE"
chown -R gpuxmine:gpuxmine "$STATE"
chmod 750 "$STATE"

STAMP="$(date +%Y%m%d%H%M%S)"
TARGET="$ROOT/releases/$STAMP"
mkdir -p "$TARGET"

echo "==> ดาวน์โหลดและแตกไฟล์"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
curl -fsSL "$ASSET_URL" -o "$TMP/relay.zip"

if command -v unzip >/dev/null; then
    unzip -q "$TMP/relay.zip" -d "$TARGET"
else
    # บางเครื่องไม่มี unzip แต่ bsdtar/python3 มักมี
    (cd "$TARGET" && (bsdtar -xf "$TMP/relay.zip" || python3 -m zipfile -e "$TMP/relay.zip" .))
fi

BINARY="$TARGET/GpuxMine.Relay"
[[ -f "$BINARY" ]] || { echo "ไม่พบไฟล์ GpuxMine.Relay ในแพ็กเกจ" >&2; exit 1; }
chmod +x "$BINARY"
chown -R root:root "$TARGET"

echo "==> ตั้งค่า admin key"
if [[ ! -f "$ENV_FILE" ]]; then
    # กุญแจนี้ออก worker ใหม่ได้ทั้งเครือข่าย สร้างให้แรงและอ่านได้เฉพาะ root
    KEY="$(head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n')"
    printf 'GPUXMINE_ADMIN_KEY=%s\n' "$KEY" > "$ENV_FILE"
    chmod 600 "$ENV_FILE"
    echo
    echo "    สร้าง admin key ใหม่แล้ว — เอาค่านี้ไปใส่ใน .env ของ xmanstudio:"
    echo
    echo "      GPUXMINE_RELAY_ADMIN_KEY=$KEY"
    echo
else
    echo "    ใช้ค่าเดิมใน $ENV_FILE"
fi

echo "==> ติดตั้ง systemd unit"
install -m 644 "$(dirname "$0")/gpuxmine-relay.service" "/etc/systemd/system/$SERVICE.service"

# สลับ symlink เป็นขั้นตอนสุดท้าย: จนถึงบรรทัดนี้ของเดิมยังรับงานอยู่ตามปกติ
ln -sfn "$TARGET" "$ROOT/current"

systemctl daemon-reload
systemctl enable "$SERVICE" >/dev/null
systemctl restart "$SERVICE"

echo "==> รอให้ขึ้น"
for i in $(seq 1 20); do
    if curl -fsS http://127.0.0.1:5080/healthz >/dev/null 2>&1; then
        echo "    ตอบแล้ว: $(curl -fsS http://127.0.0.1:5080/healthz)"
        break
    fi
    sleep 1
    if [[ $i -eq 20 ]]; then
        echo "    ยังไม่ตอบ — ดู: journalctl -u $SERVICE -n 50 --no-pager" >&2
        exit 1
    fi
done

# เก็บไว้สองรุ่น: รุ่นที่รันอยู่ กับรุ่นก่อนหน้าเผื่อถอย
ls -1dt "$ROOT/releases"/* 2>/dev/null | tail -n +3 | xargs -r rm -rf

echo
echo "เสร็จแล้ว"
echo "  สถานะ   : systemctl status $SERVICE"
echo "  ล็อก    : journalctl -u $SERVICE -f"
echo "  ต่อไป   : ตั้ง reverse proxy ตาม deploy/relay/nginx-relay.conf แล้วออกใบรับรองให้ relay.xman4289.com"
