'use client';

import dynamic from 'next/dynamic';
import { useRef } from 'react';

const MonacoEditor = dynamic(() => import('@monaco-editor/react'), { ssr: false });

interface CodeEditorProps {
  value: string;
  onChange?: (value: string) => void;
  language?: string;
  readOnly?: boolean;
  height?: string;
  placeholder?: string;
}

export default function CodeEditor({
  value,
  onChange,
  language = 'csharp',
  readOnly = false,
  height = '320px',
}: CodeEditorProps) {
  const editorRef = useRef<unknown>(null);

  function handleEditorDidMount(editor: unknown) {
    editorRef.current = editor;
  }

  return (
    <div className="rounded-lg overflow-hidden border border-[#3e3e42] bg-[#1e1e1e]">
      <MonacoEditor
        height={height}
        language={language}
        value={value}
        theme="vs-dark"
        onChange={(val) => onChange?.(val || '')}
        onMount={handleEditorDidMount}
        options={{
          readOnly,
          fontSize: 14,
          fontFamily: "'Cascadia Code', 'Fira Code', 'Consolas', 'Courier New', monospace",
          fontLigatures: true,
          minimap: { enabled: false },
          scrollBeyondLastLine: false,
          lineNumbers: 'on',
          renderLineHighlight: 'all',
          tabSize: 4,
          insertSpaces: true,
          automaticLayout: true,
          wordWrap: 'on',
          padding: { top: 12, bottom: 12 },
          scrollbar: {
            verticalScrollbarSize: 8,
            horizontalScrollbarSize: 8,
          },
          suggestOnTriggerCharacters: !readOnly,
          quickSuggestions: !readOnly,
          contextmenu: true,
          folding: true,
          renderWhitespace: 'selection',
          bracketPairColorization: { enabled: true },
          guides: {
            bracketPairs: true,
            indentation: true,
          },
        }}
        loading={
          <div className="flex items-center justify-center h-full bg-[#1e1e1e] text-[#858585] text-sm">
            <span className="animate-pulse">Loading editor...</span>
          </div>
        }
      />
    </div>
  );
}
