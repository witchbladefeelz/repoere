#!/usr/bin/env python3
import tkinter as tk
from tkinter import messagebox, scrolledtext
import hashlib, os, shutil, platform, getpass, multiprocessing, requests, json

ACTIVATE_URL = "http://localhost:8080/activate-license.php"
CHECK_URL = "http://localhost:8080/check-license.php"

def compute_hwid():
    cpu_count = multiprocessing.cpu_count()
    user = getpass.getuser()
    machine = platform.node()
    os_version = platform.platform()
    try:
        root = os.path.abspath(os.sep)
        total_size = shutil.disk_usage(root).total
    except:
        total_size = 0
    raw = f"{cpu_count}{user}{machine}{os_version}{total_size}"
    return hashlib.md5(raw.encode("ascii", errors="ignore")).hexdigest()[:20].upper()

def activate_license(hwid, key):
    try:
        r = requests.get(ACTIVATE_URL, params={"hwid": hwid, "key": key}, timeout=10)
        r.raise_for_status()
        return r.json()
    except Exception as e:
        return {"success": False, "message": f"Ошибка при активации: {e}"}

def check_license(hwid):
    try:
        r = requests.get(CHECK_URL, params={"hwid": hwid}, timeout=10)
        r.raise_for_status()
        return r.json()
    except Exception as e:
        return {"success": False, "message": f"Ошибка при проверке: {e}"}

def on_activate():
    key = entry_key.get().strip()
    if not key:
        messagebox.showwarning("Внимание", "Введите ключ активации!")
        return
    hwid = entry_hwid.get()
    result = activate_license(hwid, key)
    output_box.delete(1.0, tk.END)
    output_box.insert(tk.END, json.dumps(result, indent=2, ensure_ascii=False))
    if result.get("success"):
        messagebox.showinfo("Успешно", result.get("message", "Активация прошла успешно."))
    else:
        messagebox.showerror("Ошибка", result.get("message", "Не удалось активировать."))

def on_check():
    hwid = entry_hwid.get()
    result = check_license(hwid)
    output_box.delete(1.0, tk.END)
    output_box.insert(tk.END, json.dumps(result, indent=2, ensure_ascii=False))
    if result.get("valid"):
        messagebox.showinfo("Подписка активна", "Лицензия действительна.")
    elif result.get("success"):
        messagebox.showwarning("Подписка недействительна", result.get("message", "Истек срок или пользователь заблокирован."))
    else:
        messagebox.showerror("Ошибка", result.get("message", "Ошибка проверки лицензии."))

root = tk.Tk()
root.title("License Loader")
root.geometry("520x480")
root.resizable(False, False)

tk.Label(root, text="HWID:", font=("Segoe UI", 10, "bold")).pack(anchor="w", padx=10, pady=(10,0))
entry_hwid = tk.Entry(root, width=50)
entry_hwid.pack(padx=10, pady=5)
entry_hwid.insert(0, compute_hwid())

tk.Label(root, text="Ключ активации:", font=("Segoe UI", 10, "bold")).pack(anchor="w", padx=10, pady=(10,0))
entry_key = tk.Entry(root, width=50)
entry_key.pack(padx=10, pady=5)

frame_buttons = tk.Frame(root)
frame_buttons.pack(pady=10)
tk.Button(frame_buttons, text="Активировать", command=on_activate, width=15, bg="#4CAF50", fg="white").grid(row=0, column=0, padx=5)
tk.Button(frame_buttons, text="Проверить", command=on_check, width=15, bg="#2196F3", fg="white").grid(row=0, column=1, padx=5)

tk.Label(root, text="Вывод:", font=("Segoe UI", 10, "bold")).pack(anchor="w", padx=10)
output_box = scrolledtext.ScrolledText(root, wrap=tk.WORD, width=60, height=15, font=("Consolas", 9))
output_box.pack(padx=10, pady=(5,10))

root.mainloop()
