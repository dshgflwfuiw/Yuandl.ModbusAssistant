import React from 'react';
import { AlertCircle, CheckCircle2, Info, AlertTriangle, X } from 'lucide-react';
import { useAppContext, type SnackbarSeverity } from '../context/AppContext';

const iconMap: Record<SnackbarSeverity, React.ReactNode> = {
  info: <Info className="w-4 h-4 text-[#0078D4] shrink-0" />,
  success: <CheckCircle2 className="w-4 h-4 text-[#107C41] shrink-0" />,
  warning: <AlertTriangle className="w-4 h-4 text-[#D97706] shrink-0" />,
  error: <AlertCircle className="w-4 h-4 text-[#C42B1C] shrink-0" />,
};

export const SnackbarHost: React.FC = () => {
  const { snackbars, dismissSnackbar } = useAppContext();

  if (snackbars.length === 0) return null;

  return (
    <div className="fixed bottom-12 right-5 z-50 flex flex-col gap-2 max-w-sm pointer-events-none">
      {snackbars.map((item) => (
        <div
          key={item.id}
          className="fluent-card pointer-events-auto shadow-lg px-3.5 py-2.5 flex items-start gap-2.5 text-xs"
        >
          <div className="mt-0.5">{iconMap[item.severity]}</div>
          <div className="flex-1 min-w-0">
            <div className="font-semibold text-[var(--text-primary)]">{item.title}</div>
            <div className="text-[var(--text-secondary)] mt-0.5 break-words">
              {item.message}
            </div>
          </div>
          <button
            type="button"
            onClick={() => dismissSnackbar(item.id)}
            className="text-[var(--text-secondary)] hover:text-[var(--text-primary)] p-0.5 cursor-pointer"
            aria-label="关闭通知"
          >
            <X className="w-3.5 h-3.5" />
          </button>
        </div>
      ))}
    </div>
  );
};
