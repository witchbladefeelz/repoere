<?php
header('Content-Type: application/json; charset=utf-8');

$host = getenv('DB_HOST') ?: 'localhost';
$port = getenv('DB_PORT') ?: '3306';
$dbname = getenv('DB_NAME') ?: 'sentinel';
$username = getenv('DB_USER') ?: 'root';
$password = getenv('DB_PASSWORD') ?: '';

$PRIVATE_KEY_PEM = "-----BEGIN PRIVATE KEY-----
MIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQDHgjyFx9AhcE1M
26SSfAmjCAYNQog1n5YkPrl/RgGZAP9i/cNCC1lHEze0CL7hQ8jBBNbnuN86M8c0
CANFgVJxrtlv2wAbXiDqJOLqWxrUOygXLOXtMpfqHhQUOHB5psOE1oLDdDS6f2/T
b14hxX3eMCQHfs0yfEkQDcuVXgGTitpYzSy4m9AJAbS+F4TpSj7+sWeuvd3+x91w
3SDV54u3I2u2lKzexK7SIS1C0St6c2kXUSFybfGWmueEfCXewJlGfjS/Gu155UeH
AvNAC3FUes1I0DtwlQSXgpdv/q/SmgACvhPh7YRYV4WcRgDO8psFKMsaeeZWHsg1
D9pVwxUhAgMBAAECggEAEGOYJIaWiA4D+5qreUZr5ZL6KaAGinPSpKWWcr3F0dOC
0eygsU3iBp8DMMHrKZwk415KEn0IXBf2zEUVkmHI660pEDGVtP8q2waEBtZ3F2od
+qa7OJyzCA4dKNATb/A4GDMlniLEq3Fei/2WZlXP54Rdi/6+99+9LMQarj7WXaVM
Ribb/lkwNPU/PNeM5NV+D6BIF3esn2M+QbTo62649ac2lqGAdH2ZlZqcqyliY33u
gt+DfzZ8d8ddoGrrDObBxYFK5ZqW2IHLev81gwlnnwFK0fB0d813DwEvGLwlt+2U
4D6VVHbMb7rJ4J0vvtWdCJkiEBRgJPBWVh3Ax17jxQKBgQDpcn5AJyUK/irD/NtP
RPop7natRPUlO8g8VWhqWHEgrMuDoBUukO7Zpik/4bqUwRAFjqYdgF7vKn+VwVGr
pVz9wPfxL5qDQt3qyupJ+3K5kKrbL/t2YwXS7PH5vJJtoG60c3JrbavVMgtWW+Au
wv8gY1cP5ui/9AicRnERXGWYUwKBgQDayGRpDCdA4/f6sFU328VZPwkE07UodOTk
B7LI9JRfY2obc1x92EEazwtEhQzgxG/EkEJaeFgYVLAOZH9JXa4KbihtHAIdAMYn
fL28IlT5PUMjkis24N1HnNOwrXu0/zYtiA/ProAv5KqLf2Zr6+Z865xhCMACSuwP
UP/fspveOwKBgDbzt5pcXJDo4aI+7FUNlKG4O4FwARDhsLHbHPgjl4Wshz+VuEa8
4Syku4MJHMWVaLMWMC4zoKVF6MCUwCfahjhJa1P/86xAWkLBv0LpCMl7r2xnMBdf
Gejb094IsKNTp5ucrWtyZoeJ/zc162C2kB3MpJrerR06UaiPwF/o0xV3AoGBAI5y
Ft5GaXBYfWadVH7P+ogHAKpB5Rt4MGc/k+o/RDNvGPDShY/yM0FvOJjeP+pAO70x
Z+JbpJAC47YbPLzZ360u1+diawXTMTEYiLhragP1HTeVvck3UxuDQlkwOvE3kWDH
y/OeXBvSuC+nPVpa93CyPtj2x302pse6Jz7QnadtAoGANHDBFonU4abCQXhwLTBc
oRgqwlTjTp+d4wt6z44sXyxVP5ziP+xQy1FaroMxcrJJqKs/l3MiQqk2PG6OIORU
HcwUNEQnGyuUnLQCizK+q/BvFlMm+T7XSpLOpppwC/K70RrEGI/bKuDGHGBVcSlv
2RdZwitUKiokACdgTn27lP8=
-----END PRIVATE KEY-----";

function rsa_sign_base64(string $data, string $private_pem): ?string {
    $pkey = openssl_pkey_get_private($private_pem);
    if ($pkey === false) return null;
    $ok = openssl_sign($data, $signature, $pkey, OPENSSL_ALGO_SHA256);
    openssl_free_key($pkey);
    return $ok ? base64_encode($signature) : null;
}

try {
    $pdo = new PDO("mysql:host=$host;port=$port;dbname=$dbname;charset=utf8", $username, $password);
    $pdo->setAttribute(PDO::ATTR_ERRMODE, PDO::ERRMODE_EXCEPTION);
} catch(PDOException $e) {
    http_response_code(500);
    echo json_encode(['valid' => false, 'message' => 'Database connection failed']);
    exit;
}

$hwid = trim($_REQUEST['hwid'] ?? '');
$nonce = trim($_REQUEST['nonce'] ?? '');

if ($hwid === '') {
    http_response_code(400);
    echo json_encode(['valid' => false, 'message' => 'HWID not specified']);
    exit;
}
if ($nonce === '') {
    http_response_code(400);
    echo json_encode(['valid' => false, 'message' => 'Nonce not specified']);
    exit;
}
if (strlen($hwid) > 255 || strlen($nonce) > 255) {
    http_response_code(400);
    echo json_encode(['valid' => false, 'message' => 'Parameters too long']);
    exit;
}

try {
    $stmt = $pdo->prepare("SELECT id, subscription, banned FROM users WHERE hwid = ? LIMIT 1");
    $stmt->execute([$hwid]);
    $user = $stmt->fetch(PDO::FETCH_ASSOC);

    if (!$user) {
        $current_time = time();
        $timestamp = $current_time;
        $signed_string = "{$hwid}||0|{$nonce}|{$timestamp}";
        $signature = rsa_sign_base64($signed_string, $PRIVATE_KEY_PEM);

        echo json_encode([
            'valid' => false,
            'message' => 'HWID not found',
            'hwid' => $hwid,
            'expiry' => null,
            'banned' => null,
            'expired' => null,
            'days_remaining' => 0,
            'timestamp' => $timestamp,
            'nonce' => $nonce,
            'signature' => $signature
        ]);
        exit;
    }

    $current_time = time();
    $expiry_time = strtotime($user['subscription']);
    $is_banned = (bool)$user['banned'];
    $is_expired = $expiry_time <= $current_time;
    $is_valid = !$is_banned && !$is_expired;
    $days_remaining = max(0, (int)floor(($expiry_time - $current_time) / 86400));
    $subscription_iso = date('c', $expiry_time);
    $timestamp = $current_time;
    $signed_string = "{$hwid}|{$subscription_iso}|" . ($is_valid ? '1' : '0') . "|{$nonce}|{$timestamp}";
    $signature_b64 = rsa_sign_base64($signed_string, $PRIVATE_KEY_PEM);

    if ($signature_b64 === null) {
        http_response_code(500);
        echo json_encode(['valid' => false, 'message' => 'Signing failed']);
        exit;
    }

    $response = [
        'valid' => $is_valid,
        'message' => $is_valid ? 'Subscription valid' : ($is_banned ? 'User banned' : 'Subscription expired'),
        'hwid' => $hwid,
        'expiry' => $subscription_iso,
        'banned' => $is_banned,
        'expired' => $is_expired,
        'days_remaining' => $days_remaining,
        'timestamp' => $timestamp,
        'nonce' => $nonce,
        'signature' => $signature_b64
    ];

    echo json_encode($response, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE);
    exit;

} catch(PDOException $e) {
    http_response_code(500);
    echo json_encode(['valid' => false, 'message' => 'Database error']);
    exit;
}