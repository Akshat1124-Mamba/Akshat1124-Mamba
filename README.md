# DotNet AI Debugger 🐛⚡

> **AI-powered .NET code analysis tool** — like having a Senior .NET Architect review your code in real-time.

[![Next.js](https://img.shields.io/badge/Next.js-16-black?logo=next.js)](https://nextjs.org)
[![TypeScript](https://img.shields.io/badge/TypeScript-5-blue?logo=typescript)](https://www.typescriptlang.org)
[![GPT-5.2](https://img.shields.io/badge/AI-GPT--5.2-green?logo=openai)](https://openai.com)
[![MongoDB](https://img.shields.io/badge/Database-MongoDB-green?logo=mongodb)](https://mongodb.com)

---

## 🎯 What is DotNet AI Debugger?

A full-stack web application that helps .NET developers debug, analyze, and improve their code using GPT-5.2. It delivers structured, actionable analysis like a senior architect — not generic chatbot advice.

---

## ✨ Features

### 🧩 Core Features
- **Monaco Code Editor** — Full VS Code-like editor with C# syntax highlighting, line numbers, and bracket matching
- **Structured AI Analysis** — 7 clearly separated analysis cards with actionable insights
- **Debug History** — Persistent MongoDB storage of last 10 debug sessions
- **Copy to Clipboard** — Copy button on every section and code block

### 🧠 Analysis Sections
| Section | Description |
|---------|-------------|
| 🔍 Problem Explanation | What is wrong with the code |
| 📍 Root Cause | Why it's happening |
| 🛠 Suggested Fix | How to fix it |
| 💻 Corrected Code | Clean, compilable corrected code in Monaco editor |
| ⚡ Performance Improvements | LINQ, async, memory optimizations |
| 🔐 Security Issues | XSS, SQL injection, auth gaps |
| 📈 Best Practices | SOLID, patterns, naming conventions |

### 🔧 Debug Modes
| Mode | Focus Area |
|------|-----------|
| **Quick Debug** | Find and fix the main error fast |
| **Deep Architectural Review** | SOLID, layers, maintainability |
| **Performance Audit** | LINQ, async/await, memory, DB calls |
| **Security Audit** | XSS, SQL injection, auth, validation |
| **Refactor Suggestion** | Patterns, DRY, modern C# features |

### 🌟 Advanced Features
- **Junior Developer Mode** — Simplified explanations with examples
- **AI Confidence Score** — Realistic confidence rating per analysis
- **Token Usage Display** — Input/output tokens + estimated cost
- **Collapsible Sections** — Clean, distraction-free UI
- **Coming Soon: Full Project Upload** — .sln / .csproj analysis

---

## 🛠 Tech Stack

| Layer | Technology |
|-------|-----------|
| **Frontend** | Next.js 16, React 19, TypeScript |
| **Styling** | Tailwind CSS, VS Code dark theme |
| **Code Editor** | Monaco Editor (same as VS Code) |
| **AI** | OpenAI GPT-5.2 (via Emergent LLM) |
| **Database** | MongoDB (with in-memory fallback) |
| **Icons** | Lucide React |

---

## 🚀 Getting Started

### Prerequisites
- Node.js 18+
- MongoDB (optional — falls back to in-memory)
- OpenAI API key (or Emergent LLM key)

### Installation

```bash
git clone https://github.com/Akshat1124-Mamba/Akshat1124-Mamba.git
cd Akshat1124-Mamba
npm install
```

### Environment Setup

Create `.env.local`:

```env
MONGODB_URI=mongodb://localhost:27017/dotnet-ai-debugger
OPENAI_API_KEY=your_api_key_here
OPENAI_BASE_URL=https://www.genspark.ai/api/llm_proxy/v1
```

### Run Development Server

```bash
npm run dev
```

Open [http://localhost:3000](http://localhost:3000)

---

## 📐 Project Structure

```
├── app/
│   ├── api/
│   │   ├── analyze/route.ts      # AI analysis endpoint
│   │   └── sessions/
│   │       ├── route.ts          # Session CRUD
│   │       └── [id]/route.ts     # Session detail
│   ├── globals.css               # VS Code theme styles
│   ├── layout.tsx                # Root layout
│   └── page.tsx                  # Main application page
├── components/
│   ├── AnalysisResults.tsx       # Result display with all 7 sections
│   ├── CodeEditor.tsx            # Monaco editor wrapper
│   ├── CollapsibleSection.tsx    # Collapsible UI cards
│   ├── CopyButton.tsx            # Copy to clipboard
│   └── SessionHistory.tsx        # Sidebar session list
├── lib/
│   ├── mongodb.ts                # DB connection with fallback
│   ├── prompts.ts                # AI system/user prompt builders
│   └── types.ts                  # TypeScript types & constants
└── models/
    └── DebugSession.ts           # Mongoose schema
```

---

## 🤖 AI System Behavior

The AI uses a carefully engineered system prompt that:

1. **Validates Razor block matching** first (for MVC views)
2. **Detects null reference risks** 
3. **Identifies incorrect async/await patterns**
4. **Flags inefficient LINQ inside loops**
5. **Adjusts depth based on selected mode**
6. **Returns strict JSON** — no markdown, no prose wrapping

The response is always structured JSON parsed directly into UI cards.

---

## 💡 Usage Tips

- Paste your complete class/method — more context = higher confidence score
- Include the full stack trace for runtime errors
- Use **Security Audit** mode for code that handles user input
- Use **Performance Audit** for code with database queries
- Enable **Junior Mode** for learning-focused explanations

---

## 📸 UI Design

- VS Code-inspired dark theme (`#1e1e1e` background)
- Status bar with current mode info (like VS Code)
- Tab-based navigation (Editor / Results)
- Collapsible analysis sections
- Monaco editor for both input and corrected code output
- Responsive layout with collapsible history sidebar

---

## 🔐 Security Notes

- API keys are stored in environment variables only
- No user data is logged to external services
- MongoDB connection strings are never exposed to the client

---

## 📄 License

MIT License — Use freely for personal and commercial projects.

---

*Built with ❤️ for the .NET developer community*
