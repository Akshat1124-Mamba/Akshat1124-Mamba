'use client';

import { DebugSession, DEBUG_MODE_LABELS } from '@/lib/types';
import { Clock, Cpu, Trash2 } from 'lucide-react';

interface SessionHistoryProps {
  sessions: DebugSession[];
  onSelect: (session: DebugSession) => void;
  onDelete: (id: string) => void;
  currentSessionId: string | null;
}

function formatDate(dateStr: string): string {
  const date = new Date(dateStr);
  const now = new Date();
  const diff = now.getTime() - date.getTime();

  if (diff < 60000) return 'Just now';
  if (diff < 3600000) return `${Math.floor(diff / 60000)}m ago`;
  if (diff < 86400000) return `${Math.floor(diff / 3600000)}h ago`;
  return date.toLocaleDateString();
}

export default function SessionHistory({
  sessions,
  onSelect,
  onDelete,
  currentSessionId,
}: SessionHistoryProps) {
  if (sessions.length === 0) {
    return (
      <div className="text-center py-8">
        <p className="text-[#555558] text-sm">No sessions yet</p>
        <p className="text-[#444447] text-xs mt-1">Your debug history will appear here</p>
      </div>
    );
  }

  return (
    <div className="space-y-2">
      {sessions.map((session) => (
        <div
          key={session._id}
          className={`group relative rounded-lg border p-3 cursor-pointer transition-all duration-150 ${
            currentSessionId === session._id
              ? 'bg-[#094771] border-[#1177bb]'
              : 'bg-[#252526] border-[#3e3e42] hover:bg-[#2a2d2e] hover:border-[#4e4e52]'
          }`}
          onClick={() => onSelect(session)}
        >
          <div className="flex items-start justify-between gap-2">
            <div className="flex-1 min-w-0">
              <div className="flex items-center gap-2 mb-1">
                <span className="text-xs font-medium text-[#4fc1ff] truncate">
                  {session.technology}
                </span>
                <span className="text-[#555558] text-xs">•</span>
                <span className="text-xs text-[#858585] truncate">
                  {DEBUG_MODE_LABELS[session.debugMode] || session.debugMode}
                </span>
              </div>
              <div className="flex items-center gap-3 text-xs text-[#6e7681]">
                <span className="flex items-center gap-1">
                  <Clock size={10} />
                  {formatDate(session.createdAt)}
                </span>
                {session.tokenUsage && (
                  <span className="flex items-center gap-1">
                    <Cpu size={10} />
                    {session.tokenUsage.total_tokens.toLocaleString()} tokens
                  </span>
                )}
              </div>
              <div className="mt-1.5">
                <div
                  className={`inline-flex items-center gap-1 text-xs px-1.5 py-0.5 rounded ${
                    session.result.confidence_score >= 80
                      ? 'bg-green-500/10 text-green-400'
                      : session.result.confidence_score >= 60
                      ? 'bg-yellow-500/10 text-yellow-400'
                      : 'bg-red-500/10 text-red-400'
                  }`}
                >
                  🤖 {session.result.confidence_score}%
                </div>
              </div>
            </div>
            <button
              onClick={(e) => {
                e.stopPropagation();
                onDelete(session._id);
              }}
              className="opacity-0 group-hover:opacity-100 p-1 rounded text-[#555558] hover:text-red-400 hover:bg-red-400/10 transition-all duration-150"
              title="Delete session"
            >
              <Trash2 size={12} />
            </button>
          </div>
        </div>
      ))}
    </div>
  );
}
