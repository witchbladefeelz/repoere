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

function rsa_encrypt_private(string $data, string $private_pem): ?string {
    $pkey = openssl_pkey_get_private($private_pem);
    if ($pkey === false) return null;

    $encrypted = '';
    $chunks = str_split($data, 200); // RSA can encrypt ~245 bytes with 2048-bit key

    foreach ($chunks as $chunk) {
        $ok = openssl_private_encrypt($chunk, $encrypted_chunk, $pkey);
        if (!$ok) {
            openssl_free_key($pkey);
            return null;
        }
        $encrypted .= $encrypted_chunk;
    }

    openssl_free_key($pkey);
    return base64_encode($encrypted);
}

function send_encrypted_response(array $response_data, string $private_pem) {
    $json_response = json_encode($response_data, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE);
    $encrypted_data = rsa_encrypt_private($json_response, $private_pem);

    if ($encrypted_data === null) {
        http_response_code(500);
        echo json_encode(['error' => 'Encryption failed']);
        exit;
    }

    echo json_encode([
        'encrypted' => true,
        'data' => $encrypted_data
    ]);
}

try {
    $pdo = new PDO("mysql:host=$host;port=$port;dbname=$dbname;charset=utf8", $username, $password);
    $pdo->setAttribute(PDO::ATTR_ERRMODE, PDO::ERRMODE_EXCEPTION);
} catch(PDOException $e) {
    http_response_code(500);
    die(json_encode(['success' => false, 'message' => 'Database connection failed']));
}

$hwid = trim($_REQUEST['hwid'] ?? '');
$key = trim($_REQUEST['key'] ?? '');
$nonce = trim($_REQUEST['nonce'] ?? '');

if (!$hwid || !$key || !$nonce) {
    http_response_code(400);
    die(json_encode(['success' => false, 'message' => 'Missing required parameters']));
}

if (strlen($hwid) > 255 || strlen($key) > 255 || strlen($nonce) > 255) {
    http_response_code(400);
    die(json_encode(['success' => false, 'message' => 'Parameters too long']));
}

try {
    $pdo->beginTransaction();

    $stmt = $pdo->prepare("SELECT id, days FROM subscriptions WHERE subscription_key = ? LIMIT 1");
    $stmt->execute([$key]);
    $subscription = $stmt->fetch(PDO::FETCH_ASSOC);
    if (!$subscription) {
        $pdo->rollBack();
        send_encrypted_response([
            'success' => false,
            'message' => 'Invalid subscription key'
        ], $PRIVATE_KEY_PEM);
        exit;
    }

    $user_id = $subscription['id'];
    $days = (int)$subscription['days'];

    $stmt = $pdo->prepare("SELECT * FROM users WHERE id = ? LIMIT 1");
    $stmt->execute([$user_id]);
    $existing_user = $stmt->fetch(PDO::FETCH_ASSOC);

    if ($existing_user) {
        if ((bool)$existing_user['banned']) {
            $pdo->rollBack();
            send_encrypted_response([
                'success' => false,
                'message' => 'User is banned'
            ], $PRIVATE_KEY_PEM);
            exit;
        }

        if ($existing_user['hwid'] !== $hwid) {
            $expiry_date = date('Y-m-d H:i:s', time() + ($days * 86400));
            $stmt = $pdo->prepare("UPDATE users SET hwid = ?, subscription = ? WHERE id = ?");
            $stmt->execute([$hwid, $expiry_date, $user_id]);
            $is_new_user = true;
        } else {
            $current_subscription = strtotime($existing_user['subscription']);
            $base = ($current_subscription > time()) ? $current_subscription : time();
            $expiry_date = date('Y-m-d H:i:s', $base + ($days * 86400));
            $stmt = $pdo->prepare("UPDATE users SET subscription = ? WHERE id = ?");
            $stmt->execute([$expiry_date, $user_id]);
            $is_new_user = false;
        }
    } else {
        $expiry_date = date('Y-m-d H:i:s', time() + ($days * 86400));
        $stmt = $pdo->prepare("INSERT INTO users (id, hwid, subscription, banned) VALUES (?, ?, ?, 0)");
        $stmt->execute([$user_id, $hwid, $expiry_date]);
        $is_new_user = true;
    }

    $stmt = $pdo->prepare("DELETE FROM subscriptions WHERE subscription_key = ?");
    $stmt->execute([$key]);
    $pdo->commit();

    $timestamp = time();
    $expiry_iso = date('c', strtotime($expiry_date));
    $signed_string = "{$hwid}|{$expiry_iso}|1|{$nonce}|{$timestamp}";
    $signature_b64 = rsa_sign_base64($signed_string, $PRIVATE_KEY_PEM);

    $response_data = [
        'success' => true,
        'message' => $is_new_user ? 'Subscription activated successfully' : 'Subscription extended successfully',
        'user' => [
            'id' => $user_id,
            'hwid' => $hwid,
            'subscription' => $expiry_date,
            'banned' => false
        ],
        'expiry' => $expiry_iso,
        'nonce' => $nonce,
        'timestamp' => $timestamp,
        'signature' => $signature_b64
    ];

    send_encrypted_response($response_data, $PRIVATE_KEY_PEM);

} catch(PDOException $e) {
    $pdo->rollBack();
    send_encrypted_response([
        'success' => false,
        'message' => 'Database error'
    ], $PRIVATE_KEY_PEM);
}