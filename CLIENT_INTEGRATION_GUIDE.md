# Руководство по интеграции для клиентского приложения

Это руководство поможет интегрировать систему лицензий в ваше клиентское приложение.

## Быстрый старт

### 1. Получение ключа от пользователя

Пользователь получает ключ из Telegram бота. Вам нужно:
- Попросить пользователя ввести ключ в ваше приложение
- Или интегрировать получение ключа напрямую (если возможно)

### 2. Активация ключа

```http
GET /activate-license.php?hwid=YOUR_HWID&key=SUBSCRIPTION_KEY
```

**Пример реализации:**

```python
import requests

def activate_license(hwid, key, api_url="http://your-domain.com"):
    try:
        response = requests.get(
            f"{api_url}/activate-license.php",
            params={'hwid': hwid, 'key': key},
            timeout=10
        )
        result = response.json()
        
        if result.get('success'):
            print("✅ Лицензия активирована!")
            print(f"Истекает: {result['user']['subscription']}")
            return True
        else:
            print(f"❌ Ошибка: {result.get('message')}")
            return False
    except Exception as e:
        print(f"❌ Ошибка подключения: {e}")
        return False

# Использование
hwid = get_hwid()  # Ваша функция получения HWID
key = input("Введите ключ подписки: ")
activate_license(hwid, key)
```

### 3. Проверка лицензии

Проверяйте лицензию при запуске приложения и периодически:

```http
GET /check-license.php?hwid=YOUR_HWID
```

**Пример реализации:**

```python
def check_license(hwid, api_url="http://your-domain.com"):
    try:
        response = requests.get(
            f"{api_url}/check-license.php",
            params={'hwid': hwid},
            timeout=10
        )
        result = response.json()
        
        if result.get('success') and result.get('valid'):
            days_left = result['user']['days_remaining']
            print(f"✅ Лицензия активна! Осталось дней: {days_left}")
            return True
        else:
            print(f"❌ Лицензия недействительна: {result.get('message')}")
            return False
    except Exception as e:
        print(f"❌ Ошибка подключения: {e}")
        return False

# Проверка при запуске
if not check_license(hwid):
    print("Приложение будет закрыто.")
    exit()
```

## Полный пример интеграции

```python
import requests
import hashlib
import platform
import time

class LicenseManager:
    def __init__(self, api_url):
        self.api_url = api_url
        self.hwid = self.get_hwid()
        self.check_interval = 3600  # Проверка каждый час
    
    def get_hwid(self):
        """Генерация уникального HWID на основе железа"""
        # Пример: используем MAC адрес + серийный номер диска
        try:
            import uuid
            mac = ':'.join(['{:02x}'.format((uuid.getnode() >> i) & 0xff) 
                           for i in range(0, 8*6, 8)][::-1])
            return hashlib.md5(mac.encode()).hexdigest().upper()
        except:
            # Fallback
            return hashlib.md5(
                f"{platform.node()}{platform.processor()}".encode()
            ).hexdigest().upper()
    
    def activate(self, key):
        """Активация лицензии"""
        try:
            response = requests.get(
                f"{self.api_url}/activate-license.php",
                params={'hwid': self.hwid, 'key': key},
                timeout=10
            )
            result = response.json()
            
            if result.get('success'):
                return {
                    'success': True,
                    'expires': result['user']['subscription'],
                    'message': result.get('message')
                }
            else:
                return {
                    'success': False,
                    'message': result.get('message', 'Unknown error')
                }
        except requests.exceptions.RequestException as e:
            return {
                'success': False,
                'message': f'Connection error: {str(e)}'
            }
    
    def check(self):
        """Проверка лицензии"""
        try:
            response = requests.get(
                f"{self.api_url}/check-license.php",
                params={'hwid': self.hwid},
                timeout=10
            )
            result = response.json()
            
            if result.get('success'):
                if result.get('valid'):
                    return {
                        'valid': True,
                        'days_left': result['user']['days_remaining'],
                        'expires': result['user']['subscription']
                    }
                else:
                    return {
                        'valid': False,
                        'reason': result.get('message'),
                        'banned': result['user']['banned'],
                        'expired': result['user']['expired']
                    }
            else:
                return {
                    'valid': False,
                    'reason': result.get('message', 'HWID not found')
                }
        except requests.exceptions.RequestException as e:
            return {
                'valid': False,
                'reason': f'Connection error: {str(e)}'
            }
    
    def start_periodic_check(self, callback=None):
        """Запуск периодической проверки"""
        import threading
        
        def check_loop():
            while True:
                result = self.check()
                if not result.get('valid'):
                    if callback:
                        callback(result)
                    break
                time.sleep(self.check_interval)
        
        thread = threading.Thread(target=check_loop, daemon=True)
        thread.start()

# Использование
if __name__ == "__main__":
    manager = LicenseManager("http://your-domain.com")
    
    # При первом запуске - активация
    key = input("Введите ключ подписки: ")
    activation = manager.activate(key)
    
    if activation['success']:
        print(f"✅ Лицензия активирована до {activation['expires']}")
        
        # Проверка при запуске
        check_result = manager.check()
        if check_result.get('valid'):
            print(f"✅ Лицензия активна! Осталось дней: {check_result['days_left']}")
            
            # Запуск периодической проверки
            def on_license_invalid(result):
                print(f"❌ Лицензия стала недействительной: {result['reason']}")
                # Закрыть приложение или показать сообщение
            
            manager.start_periodic_check(on_license_invalid)
            
            # Ваш код приложения здесь
            print("Приложение запущено...")
        else:
            print(f"❌ Лицензия недействительна: {check_result['reason']}")
    else:
        print(f"❌ Ошибка активации: {activation['message']}")
```

## Рекомендации

1. **Кэширование результата проверки** - не проверяйте каждый раз при каждом действии
2. **Офлайн режим** - сохраняйте результат проверки локально для работы без интернета (с ограничениями)
3. **Graceful degradation** - если сервер недоступен, можно разрешить работу с предупреждением
4. **Логирование** - логируйте все проверки для отладки
5. **Безопасность** - не храните ключи в открытом виде, используйте шифрование

## Обработка ошибок

```python
def safe_check_license(manager):
    """Безопасная проверка с обработкой всех ошибок"""
    try:
        result = manager.check()
        
        if result.get('valid'):
            return True, result
        else:
            reason = result.get('reason', 'Unknown')
            
            if 'Connection error' in reason:
                # Сервер недоступен - можно разрешить работу с предупреждением
                print("⚠️ Сервер недоступен, работа в ограниченном режиме")
                return True, None  # Разрешить работу
            elif 'banned' in reason.lower():
                # Пользователь забанен - запретить работу
                print("❌ Ваш аккаунт заблокирован")
                return False, result
            elif 'expired' in reason.lower():
                # Подписка истекла
                print("❌ Подписка истекла. Продлите подписку в боте.")
                return False, result
            else:
                # Другая ошибка
                print(f"❌ Ошибка проверки: {reason}")
                return False, result
                
    except Exception as e:
        print(f"❌ Критическая ошибка: {e}")
        return False, None
```

## Тестирование

Используйте тестовую программу `license_tester/test_license_gui.py` для проверки работы API перед интеграцией в клиентское приложение.

