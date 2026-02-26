import { NextRequest, NextResponse } from 'next/server';
import OpenAI from 'openai';
import fs from 'fs';
import yaml from 'js-yaml';
import path from 'path';
import os from 'os';
import connectToDatabase from '@/lib/mongodb';
import DebugSession from '@/models/DebugSession';
import { buildSystemPrompt, buildUserPrompt } from '@/lib/prompts';
import { DebugMode, Technology, AnalysisResult } from '@/lib/types';

// Load OpenAI config
function getOpenAIClient(): OpenAI {
  const configPath = path.join(os.homedir(), '.genspark_llm.yaml');
  let apiKey = process.env.OPENAI_API_KEY;
  let baseURL = process.env.OPENAI_BASE_URL;

  if (fs.existsSync(configPath)) {
    try {
      const fileContents = fs.readFileSync(configPath, 'utf8');
      const config = yaml.load(fileContents) as { openai?: { api_key?: string; base_url?: string } };
      if (config?.openai?.api_key && !config.openai.api_key.startsWith('${')) {
        apiKey = config.openai.api_key;
      }
      if (config?.openai?.base_url) {
        baseURL = config.openai.base_url;
      }
    } catch {
      // fallback to env vars
    }
  }

  return new OpenAI({ apiKey, baseURL });
}

async function callOpenAI(
  client: OpenAI,
  systemPrompt: string,
  userPrompt: string,
  retryWithJsonInstruction = false
): Promise<{ content: string; usage: { prompt_tokens: number; completion_tokens: number; total_tokens: number } }> {
  const messages: OpenAI.Chat.ChatCompletionMessageParam[] = [
    { role: 'system', content: systemPrompt },
    { role: 'user', content: userPrompt },
  ];

  if (retryWithJsonInstruction) {
    messages.push({
      role: 'user',
      content: 'Your previous response was not valid JSON. Return valid JSON ONLY, no markdown, no code blocks, no extra text.',
    });
  }

  const completion = await client.chat.completions.create({
    model: 'gpt-5.2',
    messages,
    temperature: 0.2,
    max_tokens: 4000,
  });

  return {
    content: completion.choices[0]?.message?.content || '',
    usage: {
      prompt_tokens: completion.usage?.prompt_tokens || 0,
      completion_tokens: completion.usage?.completion_tokens || 0,
      total_tokens: completion.usage?.total_tokens || 0,
    },
  };
}

function parseAIResponse(content: string): AnalysisResult {
  // Remove markdown code blocks if present
  let cleaned = content.trim();
  if (cleaned.startsWith('```')) {
    cleaned = cleaned.replace(/^```(?:json)?\n?/, '').replace(/\n?```$/, '').trim();
  }

  const parsed = JSON.parse(cleaned);

  return {
    problem_explanation: parsed.problem_explanation || 'None identified.',
    root_cause: parsed.root_cause || 'None identified.',
    suggested_fix: parsed.suggested_fix || 'None identified.',
    corrected_code: parsed.corrected_code || '',
    performance_improvements: parsed.performance_improvements || 'None identified.',
    security_issues: parsed.security_issues || 'None identified.',
    best_practices: parsed.best_practices || 'None identified.',
    confidence_score: typeof parsed.confidence_score === 'number' ? parsed.confidence_score : 0,
  };
}

export async function POST(request: NextRequest) {
  try {
    const body = await request.json();
    const {
      code,
      errorMessage = '',
      technology,
      debugMode,
      juniorMode = false,
    }: {
      code: string;
      errorMessage?: string;
      technology: Technology;
      debugMode: DebugMode;
      juniorMode?: boolean;
    } = body;

    if (!code || !technology || !debugMode) {
      return NextResponse.json(
        { error: 'Missing required fields: code, technology, debugMode' },
        { status: 400 }
      );
    }

    const client = getOpenAIClient();
    const systemPrompt = buildSystemPrompt(debugMode, juniorMode);
    const userPrompt = buildUserPrompt(code, errorMessage, technology);

    let aiResponse: { content: string; usage: { prompt_tokens: number; completion_tokens: number; total_tokens: number } };
    let analysisResult: AnalysisResult;

    // First attempt
    aiResponse = await callOpenAI(client, systemPrompt, userPrompt, false);

    try {
      analysisResult = parseAIResponse(aiResponse.content);
    } catch {
      // Retry once with explicit JSON instruction
      try {
        aiResponse = await callOpenAI(client, systemPrompt, userPrompt, true);
        analysisResult = parseAIResponse(aiResponse.content);
      } catch {
        return NextResponse.json(
          { error: 'AI response format invalid. Please try again.' },
          { status: 422 }
        );
      }
    }

    // Save to MongoDB
    try {
      await connectToDatabase();
      const session = await DebugSession.create({
        code,
        errorMessage,
        technology,
        debugMode,
        juniorMode,
        result: analysisResult,
        tokenUsage: aiResponse.usage,
      });

      return NextResponse.json({
        success: true,
        sessionId: session._id,
        result: analysisResult,
        tokenUsage: aiResponse.usage,
      });
    } catch (dbError) {
      // Return result even if DB save fails
      console.error('DB save error:', dbError);
      return NextResponse.json({
        success: true,
        sessionId: null,
        result: analysisResult,
        tokenUsage: aiResponse.usage,
        warning: 'Analysis complete but session could not be saved.',
      });
    }
  } catch (error: unknown) {
    console.error('Analyze API error:', error);
    const message = error instanceof Error ? error.message : 'Internal server error';
    return NextResponse.json({ error: message }, { status: 500 });
  }
}
