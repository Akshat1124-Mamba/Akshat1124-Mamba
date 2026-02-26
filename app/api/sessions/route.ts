import { NextRequest, NextResponse } from 'next/server';
import connectToDatabase from '@/lib/mongodb';
import DebugSession from '@/models/DebugSession';

export async function GET(request: NextRequest) {
  try {
    await connectToDatabase();
    const { searchParams } = new URL(request.url);
    const limit = parseInt(searchParams.get('limit') || '10');

    const sessions = await DebugSession.find({})
      .sort({ createdAt: -1 })
      .limit(limit)
      .select('-code -result.corrected_code')
      .lean();

    return NextResponse.json({ sessions });
  } catch (error: unknown) {
    console.error('Sessions API error:', error);
    const message = error instanceof Error ? error.message : 'Failed to fetch sessions';
    return NextResponse.json({ error: message }, { status: 500 });
  }
}

export async function DELETE(request: NextRequest) {
  try {
    await connectToDatabase();
    const { searchParams } = new URL(request.url);
    const id = searchParams.get('id');

    if (!id) {
      return NextResponse.json({ error: 'Session ID required' }, { status: 400 });
    }

    await DebugSession.findByIdAndDelete(id);
    return NextResponse.json({ success: true });
  } catch (error: unknown) {
    console.error('Delete session error:', error);
    const message = error instanceof Error ? error.message : 'Failed to delete session';
    return NextResponse.json({ error: message }, { status: 500 });
  }
}
