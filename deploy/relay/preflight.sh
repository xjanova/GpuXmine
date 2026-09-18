#!/usr/bin/env bash
#
# ตรวจก่อนตั้งค่า reverse proxy ให้ relay — อ่านอย่างเดียว ไม่แก้อะไรทั้งนั้น
#
#   bash preflight.sh
#
# ตอบคำถามที่ต้องรู้ก่อนแตะ config ของเว็บเซิร์ฟเวอร์ที่รันเว็บจริงอยู่:
# ใช้ Apache หรือ nginx · โดเมนมีแล้วหรือยัง · ไฟล์ CUSTOM ที่โดเมนอื่น
# ใช้อยู่คือเลขอะไร · mod_proxy_wstunnel เปิดหรือยัง
#
# เดาเลข CUSTOM แล้วผิด = vhost พัง = ทุกเว็บบนเครื่องล่ม จึงต้องดูของจริง
set -uo pipefail

echo "=== เว็บเซิร์ฟเวอร์ ==="
for s in httpd apache2 nginx openlitespeed lshttpd; do
    if systemctl is-active --quiet "$s" 2>/dev/null; then
        echo "  กำลังรัน: $s"
    fi
done
command -v nginx >/dev/null 2>&1 && echo "  nginx: ติดตั้งแล้ว" || echo "  nginx: ไม่ได้ติดตั้ง"
command -v httpd >/dev/null 2>&1 || command -v apache2 >/dev/null 2>&1 \
    && echo "  apache: ติดตั้งแล้ว" || echo "  apache: ไม่ได้ติดตั้ง"

echo
echo "=== mod_proxy_wstunnel (ต้องมี ไม่งั้น websocket ไม่ผ่าน) ==="
if command -v httpd >/dev/null 2>&1; then
    httpd -M 2>/dev/null | grep -E 'proxy_module|proxy_http_module|proxy_wstunnel_module|headers_module|rewrite_module' \
        || echo "  อ่านรายการโมดูลไม่ได้"
elif command -v apache2ctl >/dev/null 2>&1; then
    apache2ctl -M 2>/dev/null | grep -E 'proxy_module|proxy_http_module|proxy_wstunnel_module|headers_module|rewrite_module' \
        || echo "  อ่านรายการโมดูลไม่ได้"
fi

echo
echo "=== โดเมน relay.xman4289.com ==="
DOM=/home/admin/domains/relay.xman4289.com
[[ -d "$DOM" ]] && echo "  มีโฟลเดอร์แล้ว: $DOM" || echo "  ยังไม่มี — สร้าง subdomain ใน DirectAdmin ก่อน"
[[ -d "$DOM/public_html" ]] && echo "  public_html: $(find "$DOM/public_html" -maxdepth 1 -type f 2>/dev/null | wc -l) ไฟล์ (ควรว่าง)"

echo
echo "=== ไฟล์ CUSTOM ที่โดเมนอื่นบนเครื่องนี้ใช้อยู่ ==="
echo "  (ai.xman4289.com พร็อกซีไป 127.0.0.1:3001 อยู่แล้ว — ใช้เลขเดียวกันนั้น)"
CUSTOM_DIR=/usr/local/directadmin/data/users/admin/domains
if [[ -d "$CUSTOM_DIR" ]]; then
    ls -1 "$CUSTOM_DIR" 2>/dev/null | grep -i 'CUSTOM' | sed 's/^/    /' || echo "    ยังไม่มีไฟล์ CUSTOM สักตัว"
    echo
    echo "  --- ตัวที่พร็อกซีอยู่แล้ว (ดูว่าเขียนยังไง) ---"
    grep -l 'ProxyPass' "$CUSTOM_DIR"/*CUSTOM* 2>/dev/null | while read -r f; do
        echo "    >>> $f"
        sed 's/^/        /' "$f"
    done
else
    echo "    ไม่พบ $CUSTOM_DIR — ไม่ใช่เครื่อง DirectAdmin?"
fi

echo
echo "=== ใบรับรอง ==="
CERT=/usr/local/directadmin/data/users/admin/domains/relay.xman4289.com.cert
[[ -f "$CERT" ]] && echo "  มีแล้ว: $CERT" || echo "  ยังไม่มี — เปิด Let's Encrypt ให้ subdomain นี้ใน DirectAdmin"

echo
echo "=== relay ทำงานอยู่หรือยัง ==="
if systemctl is-active --quiet gpuxmine-relay 2>/dev/null; then
    echo "  service: รันอยู่"
    curl -fsS http://127.0.0.1:5080/healthz 2>/dev/null | sed 's/^/  healthz: /' || echo "  healthz: ไม่ตอบ"
else
    echo "  ยังไม่ได้ติดตั้ง — รัน install.sh ก่อน"
fi

echo
echo "=== พอร์ต 5080 ==="
(ss -ltnp 2>/dev/null || netstat -ltnp 2>/dev/null) | grep ':5080' | sed 's/^/  /' || echo "  ไม่มีอะไรฟังอยู่"
