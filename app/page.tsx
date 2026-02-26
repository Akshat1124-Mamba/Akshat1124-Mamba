'use client';

import { useState, useEffect, useCallback } from 'react';
import dynamic from 'next/dynamic';
import {
  AnalysisResult,
  TokenUsage,
  DebugMode,
  Technology,
  DebugSession,
  TECHNOLOGIES,
  DEBUG_MODES,
} from '@/lib/types';
import AnalysisResults from '@/components/AnalysisResults';
import SessionHistory from '@/components/SessionHistory';
import { Zap, History, ChevronLeft, ChevronRight, AlertCircle, Bug } from 'lucide-react';

const CodeEditor = dynamic(() => import('@/components/CodeEditor'), { ssr: false });

const SAMPLE_CODE = `// Paste your .NET code here
// Example:
public class UserService
{
    private readonly ApplicationDbContext _context;
    
    public UserService(ApplicationDbContext context)
    {
        _context = context;
    }
    
    public List<User> GetActiveUsers()
    {
        var users = _context.Users.ToList();
        return users.Where(u => u.IsActive).ToList();
    }
}`;

export default function HomePage() {
  const [code, setCode] = useState('');
  const [errorMessage, setErrorMessage] = useState('');
  const [technology, setTechnology] = useState<Technology>('General C#');
  const [debugMode, setDebugMode] = useState<DebugMode>('quick_debug');
  const [juniorMode, setJuniorMode] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<AnalysisResult | null>(null);
  const [tokenUsage, setTokenUsage] = useState<TokenUsage | null>(null);
  const [currentSessionId, setCurrentSessionId] = useState<string | null>(null);
  const [sessions, setSessions] = useState<DebugSession[]>([]);
  const [sidebarOpen, setSidebarOpen] = useState(true);
  const [activeTab, setActiveTab] = useState<'editor' | 'results'>('editor');

  const fetchSessions = useCallback(async () => {
    try {
      const res = await fetch('/api/sessions?limit=10');
      if (res.ok) {
        const data = await res.json();
        setSessions(data.sessions || []);
      }
    } catch {
      // silently fail
    }
  }, []);

  useEffect(() => {
    fetchSessions();
  }, [fetchSessions]);

  const handleAnalyze = async () => {
    if (!code.trim()) {
      setError('Please enter code to analyze.');
      return;
    }

    setLoading(true);
    setError(null);
    setResult(null);
    setTokenUsage(null);

    try {
      const response = await fetch('/api/analyze', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ code, errorMessage, technology, debugMode, juniorMode }),
      });

      const data = await response.json();

      if (!response.ok) {
        throw new Error(data.error || 'Analysis failed');
      }

      setResult(data.result);
      setTokenUsage(data.tokenUsage);
      setCurrentSessionId(data.sessionId);
      setActiveTab('results');
      await fetchSessions();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'An unexpected error occurred');
    } finally {
      setLoading(false);
    }
  };

  const handleSessionSelect = async (session: DebugSession) => {
    setCurrentSessionId(session._id);
    // Fetch full session
    try {
      const res = await fetch(`/api/sessions/${session._id}`);
      if (res.ok) {
        const data = await res.json();
        if (data.session) {
          setCode(data.session.code || '');
          setErrorMessage(data.session.errorMessage || '');
          setTechnology(data.session.technology);
          setDebugMode(data.session.debugMode);
          setJuniorMode(data.session.juniorMode || false);
          setResult(data.session.result);
          setTokenUsage(data.session.tokenUsage);
          setActiveTab('results');
        }
      }
    } catch {
      // use partial data from list
      setResult(session.result);
      setTokenUsage(session.tokenUsage);
      setActiveTab('results');
    }
  };

  const handleSessionDelete = async (id: string) => {
    try {
      await fetch(`/api/sessions?id=${id}`, { method: 'DELETE' });
      setSessions((prev) => prev.filter((s) => s._id !== id));
      if (currentSessionId === id) {
        setCurrentSessionId(null);
      }
    } catch {
      // silently fail
    }
  };

  const currentDebugMode = DEBUG_MODES.find((m) => m.value === debugMode);

  return (
    <div className="flex h-screen bg-[#1e1e1e] text-[#cccccc] overflow-hidden">
      {/* Sidebar */}
      <div
        className={`flex flex-col border-r border-[#3e3e42] bg-[#252526] transition-all duration-300 ${
          sidebarOpen ? 'w-64' : 'w-0 overflow-hidden'
        }`}
      >
        <div className="flex items-center justify-between px-4 py-3 border-b border-[#3e3e42]">
          <div className="flex items-center gap-2">
            <History size={14} className="text-[#858585]" />
            <span className="text-xs font-semibold text-[#858585] uppercase tracking-wider">
              Debug History
            </span>
          </div>
          <span className="text-xs text-[#555558] bg-[#1e1e1e] px-1.5 py-0.5 rounded border border-[#3e3e42]">
            {sessions.length}/10
          </span>
        </div>
        <div className="flex-1 overflow-y-auto p-3 scrollbar-thin">
          <SessionHistory
            sessions={sessions}
            onSelect={handleSessionSelect}
            onDelete={handleSessionDelete}
            currentSessionId={currentSessionId}
          />
        </div>
      </div>

      {/* Main Content */}
      <div className="flex flex-col flex-1 overflow-hidden">
        {/* Top Bar */}
        <header className="flex items-center justify-between px-4 py-2.5 border-b border-[#3e3e42] bg-[#323233] shrink-0">
          <div className="flex items-center gap-3">
            <button
              onClick={() => setSidebarOpen(!sidebarOpen)}
              className="p-1.5 rounded text-[#858585] hover:text-[#cccccc] hover:bg-[#3e3e42] transition-colors"
              title="Toggle sidebar"
            >
              {sidebarOpen ? <ChevronLeft size={16} /> : <ChevronRight size={16} />}
            </button>
            <div className="flex items-center gap-2">
              <Bug size={18} className="text-[#4fc1ff]" />
              <span className="font-semibold text-[#e8e8e8] text-sm">DotNet AI Debugger</span>
              <span className="text-xs px-1.5 py-0.5 rounded bg-[#4fc1ff]/10 text-[#4fc1ff] border border-[#4fc1ff]/20 font-medium">
                GPT-5.2
              </span>
            </div>
          </div>
          <div className="flex items-center gap-2">
            <div className="hidden sm:flex items-center gap-1 text-xs text-[#858585]">
              <div className="w-1.5 h-1.5 rounded-full bg-green-400" />
              <span>Connected</span>
            </div>
          </div>
        </header>

        {/* Tab Bar */}
        <div className="flex items-center border-b border-[#3e3e42] bg-[#2d2d30] px-4 shrink-0">
          <button
            onClick={() => setActiveTab('editor')}
            className={`px-4 py-2 text-xs font-medium border-b-2 transition-colors ${
              activeTab === 'editor'
                ? 'border-[#4fc1ff] text-[#4fc1ff]'
                : 'border-transparent text-[#858585] hover:text-[#cccccc]'
            }`}
          >
            📝 Code Editor
          </button>
          <button
            onClick={() => setActiveTab('results')}
            className={`px-4 py-2 text-xs font-medium border-b-2 transition-colors ${
              activeTab === 'results'
                ? 'border-[#4fc1ff] text-[#4fc1ff]'
                : 'border-transparent text-[#858585] hover:text-[#cccccc]'
            }`}
          >
            🔍 Analysis Results
            {result && (
              <span className="ml-2 w-1.5 h-1.5 rounded-full bg-green-400 inline-block" />
            )}
          </button>
        </div>

        {/* Content Area */}
        <div className="flex-1 overflow-y-auto">
          {activeTab === 'editor' ? (
            <div className="max-w-5xl mx-auto p-5 space-y-5">
              {/* Code Editor Section */}
              <div className="space-y-2">
                <div className="flex items-center justify-between">
                  <label className="text-xs font-semibold text-[#858585] uppercase tracking-wider">
                    Code Editor
                  </label>
                  <div className="flex items-center gap-2">
                    <span className="text-xs text-[#555558]">C# / ASP.NET</span>
                    {!code && (
                      <button
                        onClick={() => setCode(SAMPLE_CODE)}
                        className="text-xs text-[#4fc1ff] hover:text-[#62c9ff] transition-colors"
                      >
                        Load sample
                      </button>
                    )}
                  </div>
                </div>
                <CodeEditor
                  value={code}
                  onChange={setCode}
                  language="csharp"
                  height="380px"
                />
              </div>

              {/* Error Message */}
              <div className="space-y-2">
                <label className="text-xs font-semibold text-[#858585] uppercase tracking-wider flex items-center gap-2">
                  <AlertCircle size={12} />
                  Error Message / Stack Trace
                  <span className="text-[#444447] font-normal normal-case tracking-normal">
                    (optional)
                  </span>
                </label>
                <textarea
                  value={errorMessage}
                  onChange={(e) => setErrorMessage(e.target.value)}
                  placeholder={`System.NullReferenceException: Object reference not set to an instance of an object.\n   at MyApp.Controllers.UserController.GetUser(Int32 id) in ...\n   at ...`}
                  className="w-full h-28 bg-[#1e1e1e] border border-[#3e3e42] rounded-lg px-4 py-3 text-[#cccccc] text-sm font-mono resize-none outline-none focus:border-[#4fc1ff] transition-colors placeholder-[#3e3e42]"
                />
              </div>

              {/* Options Grid */}
              <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                {/* Technology */}
                <div className="space-y-2">
                  <label className="text-xs font-semibold text-[#858585] uppercase tracking-wider">
                    Technology
                  </label>
                  <select
                    value={technology}
                    onChange={(e) => setTechnology(e.target.value as Technology)}
                    className="w-full bg-[#3c3c3c] border border-[#3e3e42] rounded-lg px-3 py-2.5 text-sm text-[#cccccc] outline-none focus:border-[#4fc1ff] transition-colors cursor-pointer appearance-none"
                  >
                    {TECHNOLOGIES.map((tech) => (
                      <option key={tech} value={tech}>
                        {tech}
                      </option>
                    ))}
                  </select>
                </div>

                {/* Debug Mode */}
                <div className="space-y-2">
                  <label className="text-xs font-semibold text-[#858585] uppercase tracking-wider">
                    Debug Mode
                  </label>
                  <select
                    value={debugMode}
                    onChange={(e) => setDebugMode(e.target.value as DebugMode)}
                    className="w-full bg-[#3c3c3c] border border-[#3e3e42] rounded-lg px-3 py-2.5 text-sm text-[#cccccc] outline-none focus:border-[#4fc1ff] transition-colors cursor-pointer appearance-none"
                  >
                    {DEBUG_MODES.map((mode) => (
                      <option key={mode.value} value={mode.value}>
                        {mode.label}
                      </option>
                    ))}
                  </select>
                </div>
              </div>

              {/* Mode Description */}
              {currentDebugMode && (
                <div className="flex items-center gap-2 px-4 py-2.5 rounded-lg bg-[#094771]/30 border border-[#1177bb]/30 text-xs text-[#4fc1ff]">
                  <span className="text-[#1177bb]">ℹ</span>
                  <strong>{currentDebugMode.label}:</strong> {currentDebugMode.description}
                </div>
              )}

              {/* Junior Mode Toggle */}
              <div className="flex items-center gap-3 px-4 py-3 rounded-lg bg-[#252526] border border-[#3e3e42]">
                <label className="flex items-center gap-3 cursor-pointer select-none">
                  <div className="relative">
                    <input
                      type="checkbox"
                      checked={juniorMode}
                      onChange={(e) => setJuniorMode(e.target.checked)}
                      className="sr-only"
                    />
                    <div
                      className={`w-10 h-5 rounded-full transition-colors duration-200 ${
                        juniorMode ? 'bg-[#4fc1ff]' : 'bg-[#3e3e42]'
                      }`}
                    />
                    <div
                      className={`absolute top-0.5 left-0.5 w-4 h-4 bg-white rounded-full shadow transition-transform duration-200 ${
                        juniorMode ? 'translate-x-5' : 'translate-x-0'
                      }`}
                    />
                  </div>
                  <div>
                    <p className="text-sm text-[#cccccc] font-medium">
                      Explain Like I&apos;m a Junior Developer
                    </p>
                    <p className="text-xs text-[#6e7681]">
                      Simplified explanations with examples and less jargon
                    </p>
                  </div>
                </label>
              </div>

              {/* Error Display */}
              {error && (
                <div className="flex items-start gap-3 px-4 py-3 rounded-lg bg-red-500/10 border border-red-500/30 text-red-400 text-sm">
                  <AlertCircle size={16} className="mt-0.5 shrink-0" />
                  <span>{error}</span>
                </div>
              )}

              {/* Analyze Button */}
              <button
                onClick={handleAnalyze}
                disabled={loading || !code.trim()}
                className={`w-full py-3.5 px-6 rounded-xl font-semibold text-sm flex items-center justify-center gap-3 transition-all duration-200 ${
                  loading || !code.trim()
                    ? 'bg-[#3e3e42] text-[#555558] cursor-not-allowed'
                    : 'bg-[#0e639c] hover:bg-[#1177bb] text-white shadow-lg shadow-[#0e639c]/20 hover:shadow-[#1177bb]/30 active:scale-[0.98]'
                }`}
              >
                {loading ? (
                  <>
                    <div className="flex gap-1">
                      <div className="w-2 h-2 rounded-full bg-white/60 animate-bounce [animation-delay:0ms]" />
                      <div className="w-2 h-2 rounded-full bg-white/60 animate-bounce [animation-delay:150ms]" />
                      <div className="w-2 h-2 rounded-full bg-white/60 animate-bounce [animation-delay:300ms]" />
                    </div>
                    <span>AI Analyzing...</span>
                    <span className="text-white/60 text-xs font-normal">
                      (This may take a moment)
                    </span>
                  </>
                ) : (
                  <>
                    <Zap size={16} />
                    <span>Analyze with AI</span>
                    <span className="text-white/60 text-xs font-normal">GPT-5.2</span>
                  </>
                )}
              </button>
            </div>
          ) : (
            <div className="max-w-5xl mx-auto p-5">
              {result ? (
                <AnalysisResults
                  result={result}
                  tokenUsage={tokenUsage}
                  debugMode={debugMode}
                />
              ) : (
                <div className="flex flex-col items-center justify-center py-24 text-center">
                  <div className="text-6xl mb-4">🔍</div>
                  <h2 className="text-[#cccccc] text-lg font-semibold mb-2">
                    No Analysis Yet
                  </h2>
                  <p className="text-[#6e7681] text-sm mb-6">
                    Paste your .NET code in the editor and click Analyze
                  </p>
                  <button
                    onClick={() => setActiveTab('editor')}
                    className="px-4 py-2 rounded-lg bg-[#0e639c] text-white text-sm hover:bg-[#1177bb] transition-colors"
                  >
                    Go to Editor
                  </button>
                </div>
              )}
            </div>
          )}
        </div>

        {/* Status Bar */}
        <div className="flex items-center justify-between px-4 py-1 bg-[#007acc] text-white text-xs shrink-0">
          <div className="flex items-center gap-4">
            <span>DotNet AI Debugger</span>
            <span className="opacity-75">v1.0.0</span>
          </div>
          <div className="flex items-center gap-4">
            <span className="opacity-75">
              {technology} • {DEBUG_MODES.find((m) => m.value === debugMode)?.label}
            </span>
            <span className="opacity-75">UTF-8</span>
          </div>
        </div>
      </div>
    </div>
  );
}
