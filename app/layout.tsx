import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "DotNet AI Debugger — Senior .NET Architect Assistant",
  description:
    "AI-powered .NET code debugger with GPT-5.2. Debug, analyze, and optimize your C#, ASP.NET MVC, .NET Core, Entity Framework, and SQL Server code like a senior architect.",
  keywords: [
    ".NET debugger",
    "C# code review",
    "ASP.NET debugging",
    "AI code analyzer",
    "Entity Framework",
    "GPT code review",
  ],
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="en" className="dark">
      <head>
        <meta name="viewport" content="width=device-width, initial-scale=1" />
        <link rel="icon" href="/favicon.ico" />
      </head>
      <body className="bg-[#1e1e1e] text-[#cccccc] antialiased overflow-hidden">
        {children}
      </body>
    </html>
  );
}
