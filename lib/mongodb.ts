import mongoose from 'mongoose';

const MONGODB_URI = process.env.MONGODB_URI || '';

interface MongooseCache {
  conn: typeof mongoose | null;
  promise: Promise<typeof mongoose> | null;
}

declare global {
  // eslint-disable-next-line no-var
  var __mongooseCache: MongooseCache | undefined;
}

let cached: MongooseCache = global.__mongooseCache || { conn: null, promise: null };

if (!global.__mongooseCache) {
  global.__mongooseCache = cached;
}

async function getMongoURI(): Promise<string> {
  if (MONGODB_URI) return MONGODB_URI;

  // Use in-memory MongoDB as fallback
  try {
    const { MongoMemoryServer } = await import('mongodb-memory-server');
    
    // Check if we already have an instance stored
    if ((global as unknown as Record<string, unknown>).__mongoMemoryServer) {
      return (global as unknown as Record<string, { getUri: () => string }>).__mongoMemoryServer.getUri();
    }

    const mongod = await MongoMemoryServer.create();
    (global as unknown as Record<string, unknown>).__mongoMemoryServer = mongod;
    return mongod.getUri();
  } catch (error) {
    console.warn('MongoDB memory server unavailable, using fallback URI:', error);
    return 'mongodb://localhost:27017/dotnet-ai-debugger';
  }
}

async function connectToDatabase(): Promise<typeof mongoose> {
  if (cached.conn) {
    return cached.conn;
  }

  if (!cached.promise) {
    const uri = await getMongoURI();
    cached.promise = mongoose.connect(uri, {
      bufferCommands: false,
    }).then((mongooseInstance) => {
      console.log('✅ MongoDB connected');
      return mongooseInstance;
    });
  }

  try {
    cached.conn = await cached.promise;
  } catch (e) {
    cached.promise = null;
    throw e;
  }

  return cached.conn;
}

export default connectToDatabase;
