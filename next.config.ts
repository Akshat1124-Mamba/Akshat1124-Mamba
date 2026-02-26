import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  // Allow server-side packages
  serverExternalPackages: ['mongoose', 'mongodb-memory-server'],
  
  // Turbopack config (Next.js 16+ default)
  turbopack: {},
};

export default nextConfig;
