'use client';

import { AnalysisResult, TokenUsage } from '@/lib/types';
import CollapsibleSection from './CollapsibleSection';
import CodeEditor from './CodeEditor';
import CopyButton from './CopyButton';

interface AnalysisResultsProps {
  result: AnalysisResult;
  tokenUsage: TokenUsage | null;
  debugMode: string;
}

function TextContent({ text }: { text: string }) {
  if (!text || text === 'None identified.') {
    return (
      <p className="px-5 py-4 text-[#6e7681] text-sm italic">None identified.</p>
    );
  }
  return (
    <div className="px-5 py-4">
      <p className="text-[#cccccc] text-sm leading-relaxed whitespace-pre-wrap">{text}</p>
    </div>
  );
}

function estimateCost(tokenUsage: TokenUsage): string {
  // gpt-5.2 approximate pricing
  const inputCost = (tokenUsage.prompt_tokens / 1000) * 0.015;
  const outputCost = (tokenUsage.completion_tokens / 1000) * 0.06;
  return (inputCost + outputCost).toFixed(4);
}

export default function AnalysisResults({ result, tokenUsage, debugMode }: AnalysisResultsProps) {
  const confidenceColor =
    result.confidence_score >= 80
      ? 'text-green-400'
      : result.confidence_score >= 60
      ? 'text-yellow-400'
      : 'text-red-400';

  const confidenceBg =
    result.confidence_score >= 80
      ? 'bg-green-500/20 border-green-500/30'
      : result.confidence_score >= 60
      ? 'bg-yellow-500/20 border-yellow-500/30'
      : 'bg-red-500/20 border-red-500/30';

  return (
    <div className="space-y-4">
      {/* Header Bar */}
      <div className="flex items-center justify-between px-5 py-3 rounded-xl bg-[#1e1e1e] border border-[#3e3e42]">
        <div className="flex items-center gap-3">
          <div className="w-2 h-2 rounded-full bg-green-400 animate-pulse" />
          <span className="text-[#cccccc] text-sm font-medium">Analysis Complete</span>
          <span className="text-xs text-[#6e7681] bg-[#2d2d30] px-2 py-0.5 rounded border border-[#3e3e42]">
            {debugMode.replace(/_/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase())}
          </span>
        </div>
        <div className={`flex items-center gap-2 px-3 py-1 rounded-lg border text-sm font-bold ${confidenceBg} ${confidenceColor}`}>
          <span>🤖 AI Confidence:</span>
          <span>{result.confidence_score}%</span>
        </div>
      </div>

      {/* Problem Explanation */}
      <CollapsibleSection
        title="Problem Explanation"
        icon="🔍"
        defaultOpen={true}
        action={<CopyButton text={result.problem_explanation} label="Copy" />}
      >
        <TextContent text={result.problem_explanation} />
      </CollapsibleSection>

      {/* Root Cause */}
      <CollapsibleSection
        title="Root Cause"
        icon="📍"
        defaultOpen={true}
        action={<CopyButton text={result.root_cause} label="Copy" />}
      >
        <TextContent text={result.root_cause} />
      </CollapsibleSection>

      {/* Suggested Fix */}
      <CollapsibleSection
        title="Suggested Fix"
        icon="🛠"
        defaultOpen={true}
        action={<CopyButton text={result.suggested_fix} label="Copy" />}
      >
        <TextContent text={result.suggested_fix} />
      </CollapsibleSection>

      {/* Corrected Code */}
      {result.corrected_code && result.corrected_code !== 'None identified.' && (
        <CollapsibleSection
          title="Corrected Code"
          icon="💻"
          defaultOpen={true}
          badgeText="Formatted"
          badgeColor="bg-blue-500/20 text-blue-400 border-blue-500/30"
          action={<CopyButton text={result.corrected_code} label="Copy Code" />}
        >
          <div className="p-4">
            <CodeEditor
              value={result.corrected_code}
              language="csharp"
              readOnly={true}
              height="300px"
            />
          </div>
        </CollapsibleSection>
      )}

      {/* Performance Improvements */}
      <CollapsibleSection
        title="Performance Improvements"
        icon="⚡"
        defaultOpen={false}
        action={<CopyButton text={result.performance_improvements} label="Copy" />}
      >
        <TextContent text={result.performance_improvements} />
      </CollapsibleSection>

      {/* Security Issues */}
      <CollapsibleSection
        title="Security Issues"
        icon="🔐"
        defaultOpen={result.security_issues !== 'None identified.' && !!result.security_issues}
        badgeText={
          result.security_issues && result.security_issues !== 'None identified.'
            ? '⚠ Issues Found'
            : undefined
        }
        badgeColor="bg-red-500/20 text-red-400 border-red-500/30"
        action={<CopyButton text={result.security_issues} label="Copy" />}
      >
        <TextContent text={result.security_issues} />
      </CollapsibleSection>

      {/* Best Practices */}
      <CollapsibleSection
        title="Best Practice Recommendations"
        icon="📈"
        defaultOpen={false}
        action={<CopyButton text={result.best_practices} label="Copy" />}
      >
        <TextContent text={result.best_practices} />
      </CollapsibleSection>

      {/* Token Usage */}
      {tokenUsage && (
        <div className="rounded-xl border border-[#3e3e42] bg-[#1a1a1b] p-4">
          <h3 className="text-xs font-semibold text-[#858585] uppercase tracking-widest mb-3">
            🧮 AI Usage Stats
          </h3>
          <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
            <div className="bg-[#252526] rounded-lg px-3 py-2 border border-[#3e3e42]">
              <p className="text-[#6e7681] text-xs mb-1">Input Tokens</p>
              <p className="text-[#cccccc] text-sm font-mono font-semibold">
                {tokenUsage.prompt_tokens.toLocaleString()}
              </p>
            </div>
            <div className="bg-[#252526] rounded-lg px-3 py-2 border border-[#3e3e42]">
              <p className="text-[#6e7681] text-xs mb-1">Output Tokens</p>
              <p className="text-[#cccccc] text-sm font-mono font-semibold">
                {tokenUsage.completion_tokens.toLocaleString()}
              </p>
            </div>
            <div className="bg-[#252526] rounded-lg px-3 py-2 border border-[#3e3e42]">
              <p className="text-[#6e7681] text-xs mb-1">Total Tokens</p>
              <p className="text-[#cccccc] text-sm font-mono font-semibold">
                {tokenUsage.total_tokens.toLocaleString()}
              </p>
            </div>
            <div className="bg-[#252526] rounded-lg px-3 py-2 border border-[#3e3e42]">
              <p className="text-[#6e7681] text-xs mb-1">Est. Cost</p>
              <p className="text-green-400 text-sm font-mono font-semibold">
                ~${estimateCost(tokenUsage)}
              </p>
            </div>
          </div>
        </div>
      )}

      {/* Coming Soon */}
      <div className="rounded-xl border border-dashed border-[#3e3e42] bg-[#1a1a1b]/50 p-5">
        <div className="flex items-center gap-3 mb-2">
          <span className="text-2xl">🚧</span>
          <div>
            <h3 className="text-[#cccccc] font-semibold text-sm">Full Project Upload</h3>
            <p className="text-[#6e7681] text-xs mt-0.5">
              Upload entire .NET solution for deep analysis (Beta)
            </p>
          </div>
          <span className="ml-auto text-xs px-2 py-0.5 rounded-full bg-yellow-500/10 text-yellow-500 border border-yellow-500/20">
            Coming Soon
          </span>
        </div>
        <p className="text-[#555558] text-xs pl-11">
          Support for .sln, .csproj, multi-layer analysis with dependency graph visualization.
        </p>
      </div>
    </div>
  );
}
