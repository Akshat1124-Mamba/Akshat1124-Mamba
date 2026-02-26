'use client';

import { useState, ReactNode } from 'react';
import { ChevronDown, ChevronRight } from 'lucide-react';

interface CollapsibleSectionProps {
  title: string;
  icon: string;
  children: ReactNode;
  defaultOpen?: boolean;
  badgeText?: string;
  badgeColor?: string;
  copyText?: string;
  action?: ReactNode;
}

export default function CollapsibleSection({
  title,
  icon,
  children,
  defaultOpen = true,
  badgeText,
  badgeColor = 'bg-blue-500/20 text-blue-400 border-blue-500/30',
  action,
}: CollapsibleSectionProps) {
  const [isOpen, setIsOpen] = useState(defaultOpen);

  return (
    <div className="rounded-xl border border-[#3e3e42] bg-[#252526] overflow-hidden transition-all duration-200">
      <button
        onClick={() => setIsOpen(!isOpen)}
        className="w-full flex items-center justify-between px-5 py-4 hover:bg-[#2a2d2e] transition-colors duration-150 text-left"
      >
        <div className="flex items-center gap-3">
          <span className="text-xl">{icon}</span>
          <span className="font-semibold text-[#e8e8e8] text-sm tracking-wide">{title}</span>
          {badgeText && (
            <span className={`text-xs px-2 py-0.5 rounded-full border font-medium ${badgeColor}`}>
              {badgeText}
            </span>
          )}
        </div>
        <div className="flex items-center gap-2">
          {action && isOpen && <div onClick={(e) => e.stopPropagation()}>{action}</div>}
          <span className="text-[#858585]">
            {isOpen ? <ChevronDown size={16} /> : <ChevronRight size={16} />}
          </span>
        </div>
      </button>
      {isOpen && (
        <div className="border-t border-[#3e3e42]">
          {children}
        </div>
      )}
    </div>
  );
}
