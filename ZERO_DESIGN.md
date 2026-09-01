# ZERO — Zenith Execution & Reasoning Operator

> A fully offline, voice-activated AI assistant for Windows 11.  
> Powered by local LLM, Whisper speech recognition, and MCP tool integration.

---

## Table of Contents

1. [Overview](#overview)
2. [Architecture](#architecture)
3. [Tech Stack](#tech-stack)
4. [Project Structure](#project-structure)
5. [MCP Servers](#mcp-servers)
6. [Feature List](#feature-list)
7. [Voice Interaction Flow](#voice-interaction-flow)
8. [Hotkey Design](#hotkey-design)
9. [Language Support](#language-support)
10. [Development Roadmap](#development-roadmap)
11. [Hardware Requirements](#hardware-requirements)

---

## Overview

**ZERO** (Zenith Execution & Reasoning Operator) adalah AI asisten lokal yang berjalan sepenuhnya di laptop tanpa koneksi internet. ZERO menerima perintah via suara atau teks, memproses intent menggunakan LLM lokal, dan mengeksekusi aksi nyata di sistem operasi melalui MCP tool servers.

| Atribut | Detail |
|---------|--------|
| Platform | Windows 11 |
| Mode Input | Voice (hotkey trigger) + Text |
| Mode Output | Text-to-Speech + Console/Tray notification |
| Koneksi Internet | ❌ Tidak dibutuhkan sama sekali |
| Bahasa | Indonesia & English |

---

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                        USER                             │
│              [Hotkey] atau [Text Input]                 │
└───────────────────────┬─────────────────────────────────┘
                        │
                        ▼
┌─────────────────────────────────────────────────────────┐
│                  ZERO CORE (Orchestrator)               │
│                                                         │
│   ┌─────────────┐    ┌──────────────┐                  │
│   │ Whisper STT │───▶│  Input Router│                  │
│   │  (CUDA)     │    │              │                  │
│   └─────────────┘    └──────┬───────┘                  │
│                             │                           │
│                             ▼                           │
│                   ┌──────────────────┐                  │
│                   │  Ollama Client   │                  │
 │                   │  qwen3:32b       │                  │
│                   │  (Local LLM)     │                  │
│                   └────────┬─────────┘                  │
│                            │ Tool Call                  │
│                            ▼                            │
│              ┌─────────────────────────┐                │
│              │     MCP Tool Router     │                │
│              └──┬──────┬──────┬───────┘                │
│                 │      │      │                         │
│   ┌─────────────▼┐ ┌───▼────┐ ┌▼──────────────┐       │
│   │ FileManager  │ │System  │ │  (future MCP)  │       │
│   │    MCP       │ │Control │ │                │       │
│   └─────────────┘ │  MCP   │ └────────────────┘       │
│                   └────────┘                            │
│                        │                                │
│                        ▼                                │
│               ┌────────────────┐                        │
│               │  TTS Engine    │                        │
│               │  (SAPI/Kokoro) │                        │
│               └────────────────┘                        │
└─────────────────────────────────────────────────────────┘
```

---

## Tech Stack

### Core

| Komponen | Teknologi | Versi | Keterangan |
|----------|-----------|-------|------------|
| Runtime | .NET | 10.0 | Console + Worker Service |
| Language | C# | 13 | |
| LLM | Ollama | Latest | Local inference server |
| LLM Model | qwen3:32b | Latest | ~20GB VRAM, offline, thinking mode |
| STT | Whisper.net | Latest | CUDA-accelerated |
| Whisper Model | `medium` | - | Akurat, support ID+EN |
| TTS | System.Speech (SAPI) | Built-in | Phase 1 |
| TTS (upgrade) | Kokoro TTS | - | Phase 2, suara lebih natural |

### MCP

| Komponen | Teknologi | Keterangan |
|----------|-----------|------------|
| MCP SDK | ModelContextProtocol 2.x | Official C# SDK |
| Transport | stdio | Spawned by ZERO Core |
| DI / Hosting | Microsoft.Extensions.Hosting | Generic Host pattern |

### System Integration

| Komponen | Library | Keterangan |
|----------|---------|------------|
| Hotkey | `RegisterHotKey` Win32 API | Global hotkey listener |
| Screenshot | `System.Drawing` / `Windows.Graphics.Capture` | Screen capture |
| Audio Control | `NAudio` | Volume, mute/unmute |
| Process Control | `System.Diagnostics.Process` | Launch/close apps |
| System Tray | `System.Windows.Forms.NotifyIcon` | Tray icon & menu |
| Clipboard | `System.Windows.Forms.Clipboard` | Get/set clipboard |
| Notifications | `Microsoft.Toolkit.Uwp.Notifications` | Windows toast |
| Hardware Info | `LibreHardwareMonitor` | CPU, RAM, Battery |
| Input Simulation | `InputSimulator` | Key/mouse automation |

---

## Project Structure

```
C:\Users\HasanKhairullah\Documents\Project\Axis\
│
├── ZERO_DESIGN.md                  ← Dokumen ini
│
├── Zero.sln                        ← Solution file
│
├── src\
│   │
│   ├── Zero.Core\                  ← Orchestrator utama
│   │   ├── Program.cs
│   │   ├── ZeroHost.cs             ← Background service
│   │   ├── HotkeyListener.cs       ← Global hotkey handler
│   │   ├── VoiceEngine\
│   │   │   ├── SpeechRecognizer.cs ← Whisper.net wrapper
│   │   │   └── SpeechSynthesizer.cs← TTS wrapper
│   │   ├── LLM\
│   │   │   ├── OllamaClient.cs     ← HTTP client ke Ollama
│   │   │   └── ToolCallRouter.cs   ← Parse & route tool calls
│   │   └── Tray\
│   │       └── TrayManager.cs      ← System tray icon
│   │
│   ├── Zero.FileManager\           ← MCP: File operations
│   │   ├── Program.cs
│   │   └── Tools\
│   │       └── FileManagerTools.cs
│   │
│   └── Zero.SystemControl\         ← MCP: OS control
│       ├── Program.cs
│       └── Tools\
│           ├── AppControlTools.cs
│           ├── AudioTools.cs
│           ├── ScreenTools.cs
│           ├── SystemInfoTools.cs
│           ├── PowerTools.cs
│           ├── ClipboardTools.cs
│           ├── NotificationTools.cs
│           └── InputTools.cs
│
├── config\
│   ├── zero.config.json            ← Konfigurasi ZERO
│   └── app-aliases.json            ← Mapping nama app → path exe
│
└── .bob\
    └── mcp.json                    ← Registrasi MCP ke Bob
```

---

## MCP Servers

ZERO memiliki **2 MCP server** yang dijalankan sebagai child process oleh Core:

### 1. Zero.FileManager

Operasi file system lengkap.

| Tool | Deskripsi |
|------|-----------|
| `read_file` | Baca isi file teks |
| `write_file` | Tulis/append file |
| `list_directory` | List isi folder |
| `search_files` | Cari file by nama/konten |
| `delete_file` | Hapus file |
| `read_pdf` | Ekstrak teks dari PDF (PdfPig) |

### 2. Zero.SystemControl

Kontrol penuh terhadap sistem operasi Windows.

#### App Control
| Tool | Deskripsi | Parameter |
|------|-----------|-----------|
| `launch_app` | Buka aplikasi | `appName: string` |
| `close_app` | Tutup aplikasi | `appName: string` |
| `list_running_apps` | Daftar proses yang berjalan | - |
| `focus_window` | Fokus ke window tertentu | `windowTitle: string` |
| `minimize_window` | Minimize window | `windowTitle: string` |

#### Screen
| Tool | Deskripsi | Parameter |
|------|-----------|-----------|
| `take_screenshot` | Ambil screenshot | `savePath?: string` |
| `get_screen_info` | Info resolusi & monitor | - |

#### Audio
| Tool | Deskripsi | Parameter |
|------|-----------|-----------|
| `set_volume` | Set volume sistem | `level: int (0-100)` |
| `get_volume` | Ambil volume saat ini | - |
| `mute` | Mute audio | - |
| `unmute` | Unmute audio | - |

#### System Info
| Tool | Deskripsi | Parameter |
|------|-----------|-----------|
| `get_cpu_usage` | % pemakaian CPU | - |
| `get_ram_usage` | RAM used/total | - |
| `get_battery_status` | % baterai + status charging | - |
| `get_disk_usage` | Sisa storage per drive | - |

#### Power
| Tool | Deskripsi | Parameter |
|------|-----------|-----------|
| `lock_screen` | Kunci layar | - |
| `shutdown` | Shutdown Windows | `delaySeconds?: int` |
| `restart` | Restart Windows | `delaySeconds?: int` |
| `sleep` | Sleep mode | - |
| `cancel_shutdown` | Batalkan scheduled shutdown | - |

#### Clipboard
| Tool | Deskripsi | Parameter |
|------|-----------|-----------|
| `get_clipboard` | Ambil teks dari clipboard | - |
| `set_clipboard` | Set teks ke clipboard | `text: string` |

#### Notification
| Tool | Deskripsi | Parameter |
|------|-----------|-----------|
| `send_notification` | Kirim Windows toast notification | `title: string`, `message: string` |

#### Input Automation
| Tool | Deskripsi | Parameter |
|------|-----------|-----------|
| `type_text` | Ketik teks di window aktif | `text: string` |
| `press_key` | Tekan kombinasi key | `keys: string` (e.g. `"ctrl+c"`) |

---

## Voice Interaction Flow

```
1. User tekan hotkey  (Ctrl+Shift+Space)
                │
                ▼
2. Microphone aktif  (indikator: tray icon berubah warna)
                │
                ▼
3. User bicara       "Zero, buka Chrome dan screenshot layarnya"
                │
                ▼
4. Whisper STT       → "Zero, buka Chrome dan screenshot layarnya"
   (CUDA, ~200ms)
                │
                ▼
5. Ollama LLM        → parse intent → tool calls:
   (qwen2.5:32b)       1. launch_app("chrome")
                        2. take_screenshot()
                │
                ▼
6. MCP Tool Router   → eksekusi tools secara sequential
                │
                ▼
7. TTS Response      "Chrome sudah dibuka dan screenshot disimpan."
                │
                ▼
8. Microphone nonaktif
```

---

## Hotkey Design

| Hotkey | Fungsi |
|--------|--------|
| `Ctrl + Shift + Space` | Toggle voice input (push-to-talk) |
| `Ctrl + Shift + Z` | Buka ZERO text input window |
| `Ctrl + Shift + X` | Cancel / interrupt response |

Hotkey dapat dikustomisasi di `config/zero.config.json`.

---

## Language Support

ZERO mendukung **Bahasa Indonesia dan English** secara natural, tanpa perlu setting khusus. Model `qwen2.5:32b` sudah mendukung keduanya.

**Contoh perintah Indonesia:**
```
"Zero, buka Visual Studio Code"
"Zero, screenshot layar dan simpan ke Desktop"
"Zero, volume 70 persen"
"Zero, RAM gw lagi berapa persen?"
"Zero, shutdown 5 menit lagi"
```

**Contoh perintah English:**
```
"Zero, open Chrome"
"Zero, take a screenshot"
"Zero, what's my battery level?"
"Zero, set volume to 50"
"Zero, lock the screen"
```

**Campur (code-switching):**
```
"Zero, open Chrome terus screenshot layarnya"
"Zero, battery gw berapa persen?"
```

---

## Development Roadmap

### Phase 1 — Foundation ✅
- [x] Setup solution `Zero.sln` dengan semua project
- [x] Implementasi `Zero.FileManager` MCP server
- [x] Implementasi `Zero.SystemControl` MCP server
- [x] Setup Ollama + pull model `qwen3:32b`
- [x] Implementasi `OllamaClient` di `Zero.Core`
- [x] Implementasi `ToolCallRouter` (parse tool calls dari LLM)
- [x] Integrasi MCP servers ke Core via stdio

### Phase 2 — Voice ✅
- [x] Integrasi Whisper.net + CUDA (STT)
- [x] Implementasi `HotkeyListener` (global hotkey Win32)
- [x] Integrasi Windows SAPI TTS
- [x] End-to-end voice pipeline (Hotkey → Record → STT → LLM → TTS)

### Phase 3 — Polish ✅
- [x] System tray icon + menu (warna dinamis: biru/hijau/amber)
- [x] `zero.config.json` untuk konfigurasi
- [x] `app-aliases.json` untuk mapping nama app
- [x] Error handling & structured logging yang proper
- [x] Upgrade TTS ke Kokoro TTS (KokoroSharp, GPU, af_heart voice)
- [x] Installer / startup on boot (StartupManager via registry HKCU)

### Phase 4 — Hotkeys & UI ✅
- [x] `Ctrl+Shift+Z` — text input popup window (WinForms overlay, always-on-top)
- [x] `Ctrl+Shift+X` — cancel / interrupt active LLM + TTS response

---

## Hardware Requirements

| Komponen | Minimum | Recommended | Laptop Ini |
|----------|---------|-------------|------------|
| CPU | 8 cores | 12+ cores | ✅ Ultra 9 285H (16c) |
| RAM | 16 GB | 32 GB | ✅ 32 GB |
| VRAM | 8 GB | 16 GB | ✅ 26 GB |
| GPU | NVIDIA (CUDA) | RTX series | ✅ RTX PRO 2000 Blackwell |
| Storage | 50 GB free | 100 GB free | - |
| OS | Windows 10 | Windows 11 | ✅ Win 11 Enterprise |

> **Catatan:** Dengan spesifikasi laptop yang ada, ZERO dapat menjalankan
> model `qwen3:32b` secara penuh di GPU dengan response time < 2 detik.

---

*ZERO — Zenith Execution & Reasoning Operator*  
*Designed for offline-first, privacy-first AI assistance.*
