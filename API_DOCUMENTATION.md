# API Документация для клиентского приложения

PHP файлы служат API между Telegram ботом и клиентским приложением.

## Архитектура системы

```
Telegram Bot (bot.py)
    ↓ создает ключи
База данных (MySQL)
    ↓ хранит ключи и пользователей
PHP API (activate-license.php, check-license.php)
    ↓ обрабатывает запросы
Клиентское приложение
```

## API Endpoints

### 1. Активация лицензии

**URL:** `activate-license.php`

**Метод:** GET или POST

**Параметры:**
- `hwid` (обязательный) - Hardware ID клиента
- `key` (обязательный) - Ключ подписки, полученный из бота

**Пример запроса:**
```
GET http://your-domain.com/activate-license.php?hwid=ABC123XYZ&key=KEYRCBFF06QOBT6F72V
```

**Успешный ответ (200 OK):**
```json
{
  "success": true,
  "message": "Subscription activated successfully",
  "user": {
    "id": "8123918703",
    "hwid": "ABC123XYZ",
    "subscription": "2025-12-11 01:44:24",
    "banned": false
  }
}
```

**Ошибки:**
- `400 Bad Request` - Отсутствуют обязательные параметры
- `404 Not Found` - Неверный ключ подписки
- `403 Forbidden` - Пользователь забанен
- `409 Conflict` - HWID уже зарегистрирован с другим аккаунтом
- `500 Internal Server Error` - Ошибка базы данных

**Примеры ошибок:**
```json
{
  "success": false,
  "message": "Invalid subscription key"
}
```

### 2. Проверка лицензии

**URL:** `check-license.php`

**Метод:** GET или POST

**Параметры:**
- `hwid` (обязательный) - Hardware ID клиента

**Пример запроса:**
```
GET http://your-domain.com/check-license.php?hwid=ABC123XYZ
```

**Успешный ответ (200 OK):**
```json
{
  "success": true,
  "message": "Subscription valid",
  "valid": true,
  "user": {
    "id": "8123918703",
    "hwid": "ABC123XYZ",
    "subscription": "2025-12-11 01:44:24",
    "banned": false,
    "expired": false,
    "days_remaining": 30
  }
}
```

**Если лицензия недействительна:**
```json
{
  "success": true,
  "message": "Subscription expired",
  "valid": false,
  "user": {
    "id": "8123918703",
    "hwid": "ABC123XYZ",
    "subscription": "2025-11-01 00:00:00",
    "banned": false,
    "expired": true,
    "days_remaining": 0
  }
}
```

**Если HWID не найден:**
```json
{
  "success": false,
  "message": "HWID not found",
  "valid": false,
  "user": null
}
```

## Поток работы

### Шаг 1: Получение ключа
1. Пользователь открывает Telegram бота
2. Выбирает "💳 Purchase Subscription"
3. Выбирает длительность (30/90/180 дней или Lifetime)
4. Получает ключ подписки (например: `KEYRCBFF06QOBT6F72V`)

### Шаг 2: Активация ключа
1. Клиентское приложение отправляет запрос:
   ```
   activate-license.php?hwid=CLIENT_HWID&key=KEYRCBFF06QOBT6F72V
   ```
2. PHP проверяет ключ в базе данных
3. Создает/обновляет запись пользователя с HWID
4. Удаляет использованный ключ
5. Отправляет уведомление в Telegram бот пользователю
6. Возвращает результат клиенту

### Шаг 3: Проверка лицензии (периодически)
1. Клиентское приложение периодически отправляет:
   ```
   check-license.php?hwid=CLIENT_HWID
   ```
2. PHP проверяет статус подписки
3. Возвращает результат:
   - `valid: true` - лицензия активна
   - `valid: false` - лицензия истекла/забанена/не найдена

## Примеры кода для клиента

### C# / .NET
```csharp
public class LicenseClient
{
    private string apiUrl = "http://your-domain.com";
    private string hwid;
    
    public LicenseClient(string hwid)
    {
        this.hwid = hwid;
    }
    
    public async Task<bool> ActivateLicense(string key)
    {
        using (var client = new HttpClient())
        {
            var response = await client.GetAsync(
                $"{apiUrl}/activate-license.php?hwid={hwid}&key={key}"
            );
            var result = await response.Content.ReadAsStringAsync();
            var json = JsonConvert.DeserializeObject<dynamic>(result);
            return json.success == true;
        }
    }
    
    public async Task<bool> CheckLicense()
    {
        using (var client = new HttpClient())
        {
            var response = await client.GetAsync(
                $"{apiUrl}/check-license.php?hwid={hwid}"
            );
            var result = await response.Content.ReadAsStringAsync();
            var json = JsonConvert.DeserializeObject<dynamic>(result);
            return json.valid == true;
        }
    }
}
```

### Python
```python
import requests

class LicenseClient:
    def __init__(self, api_url, hwid):
        self.api_url = api_url
        self.hwid = hwid
    
    def activate_license(self, key):
        response = requests.get(
            f"{self.api_url}/activate-license.php",
            params={'hwid': self.hwid, 'key': key}
        )
        result = response.json()
        return result.get('success', False)
    
    def check_license(self):
        response = requests.get(
            f"{self.api_url}/check-license.php",
            params={'hwid': self.hwid}
        )
        result = response.json()
        return result.get('valid', False)
```

### C++
```cpp
#include <curl/curl.h>
#include <json/json.h>

bool activateLicense(const std::string& apiUrl, 
                     const std::string& hwid, 
                     const std::string& key) {
    CURL* curl = curl_easy_init();
    std::string url = apiUrl + "/activate-license.php?hwid=" + 
                      hwid + "&key=" + key;
    
    curl_easy_setopt(curl, CURLOPT_URL, url.c_str());
    
    std::string response;
    curl_easy_setopt(curl, CURLOPT_WRITEFUNCTION, writeCallback);
    curl_easy_setopt(curl, CURLOPT_WRITEDATA, &response);
    
    CURLcode res = curl_easy_perform(curl);
    curl_easy_cleanup(curl);
    
    // Парсинг JSON ответа
    Json::Value root;
    Json::Reader reader;
    reader.parse(response, root);
    
    return root["success"].asBool();
}
```

## Безопасность

1. **HTTPS:** Используйте HTTPS для защиты данных в продакшене
2. **Валидация HWID:** Убедитесь, что HWID генерируется надежно
3. **Rate Limiting:** Добавьте ограничение на количество запросов
4. **Логирование:** Все запросы логируются в базе данных

## Уведомления в бот

При успешной активации ключа пользователь автоматически получает уведомление в Telegram:

```
✅ Key Activated!

🔑 Key: KEYRCBFF06QOBT6F72V
💻 HWID: ABC123XYZ
📅 Expires: 2025-12-11 01:44:24
👤 User ID: 8123918703

🎉 New subscription activated
```

## Обработка ошибок

Всегда проверяйте поле `success` в ответе:

```python
response = requests.get(url, params=params)
result = response.json()

if result.get('success'):
    # Успех
    user_data = result.get('user')
else:
    # Ошибка
    error_message = result.get('message')
    # Обработать ошибку
```

## Статус коды HTTP

- `200 OK` - Запрос успешно обработан
- `400 Bad Request` - Неверные параметры
- `403 Forbidden` - Пользователь забанен
- `404 Not Found` - Ключ/HWID не найден
- `409 Conflict` - HWID уже используется
- `500 Internal Server Error` - Ошибка сервера

