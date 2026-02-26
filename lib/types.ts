export type DebugMode =
  | 'quick_debug'
  | 'deep_architectural_review'
  | 'performance_audit'
  | 'security_audit'
  | 'refactor_suggestion';

export type Technology =
  | 'ASP.NET MVC'
  | '.NET Core API'
  | 'Entity Framework'
  | 'SQL Server'
  | 'LINQ'
  | 'General C#';

export interface AnalysisResult {
  problem_explanation: string;
  root_cause: string;
  suggested_fix: string;
  corrected_code: string;
  performance_improvements: string;
  security_issues: string;
  best_practices: string;
  confidence_score: number;
}

export interface TokenUsage {
  prompt_tokens: number;
  completion_tokens: number;
  total_tokens: number;
}

export interface DebugSession {
  _id: string;
  code: string;
  errorMessage: string;
  technology: Technology;
  debugMode: DebugMode;
  juniorMode: boolean;
  result: AnalysisResult;
  tokenUsage: TokenUsage;
  createdAt: string;
}

export const DEBUG_MODE_LABELS: Record<DebugMode, string> = {
  quick_debug: 'Quick Debug',
  deep_architectural_review: 'Deep Architectural Review',
  performance_audit: 'Performance Audit',
  security_audit: 'Security Audit',
  refactor_suggestion: 'Refactor Suggestion',
};

export const TECHNOLOGIES: Technology[] = [
  'ASP.NET MVC',
  '.NET Core API',
  'Entity Framework',
  'SQL Server',
  'LINQ',
  'General C#',
];

export const DEBUG_MODES: { value: DebugMode; label: string; description: string }[] = [
  {
    value: 'quick_debug',
    label: 'Quick Debug',
    description: 'Focus on identifying the main error and fix',
  },
  {
    value: 'deep_architectural_review',
    label: 'Deep Architectural Review',
    description: 'Structure, maintainability, layering, SOLID principles',
  },
  {
    value: 'performance_audit',
    label: 'Performance Audit',
    description: 'LINQ efficiency, DB calls, memory usage, async usage',
  },
  {
    value: 'security_audit',
    label: 'Security Audit',
    description: 'XSS, SQL injection, validation, authorization',
  },
  {
    value: 'refactor_suggestion',
    label: 'Refactor Suggestion',
    description: 'Cleaner patterns and improved structure',
  },
];
