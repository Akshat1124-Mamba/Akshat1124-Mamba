import mongoose, { Schema, Document, Model } from 'mongoose';

export interface IDebugSession extends Document {
  code: string;
  errorMessage: string;
  technology: string;
  debugMode: string;
  juniorMode: boolean;
  result: {
    problem_explanation: string;
    root_cause: string;
    suggested_fix: string;
    corrected_code: string;
    performance_improvements: string;
    security_issues: string;
    best_practices: string;
    confidence_score: number;
  };
  tokenUsage: {
    prompt_tokens: number;
    completion_tokens: number;
    total_tokens: number;
  };
  createdAt: Date;
}

const DebugSessionSchema = new Schema<IDebugSession>(
  {
    code: { type: String, required: true },
    errorMessage: { type: String, default: '' },
    technology: { type: String, required: true },
    debugMode: { type: String, required: true },
    juniorMode: { type: Boolean, default: false },
    result: {
      problem_explanation: { type: String, default: '' },
      root_cause: { type: String, default: '' },
      suggested_fix: { type: String, default: '' },
      corrected_code: { type: String, default: '' },
      performance_improvements: { type: String, default: '' },
      security_issues: { type: String, default: '' },
      best_practices: { type: String, default: '' },
      confidence_score: { type: Number, default: 0 },
    },
    tokenUsage: {
      prompt_tokens: { type: Number, default: 0 },
      completion_tokens: { type: Number, default: 0 },
      total_tokens: { type: Number, default: 0 },
    },
  },
  {
    timestamps: true,
  }
);

const DebugSession: Model<IDebugSession> =
  mongoose.models.DebugSession ||
  mongoose.model<IDebugSession>('DebugSession', DebugSessionSchema);

export default DebugSession;
