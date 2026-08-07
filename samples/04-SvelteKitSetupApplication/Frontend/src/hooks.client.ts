import type { HandleClientError } from "@sveltejs/kit";

declare global {
  var __runicBootError: string | undefined;
}

export const handleError: HandleClientError = ({ error }) => {
  const message = error instanceof Error
    ? `${error.name}: ${error.message}`
    : String(error);
  globalThis.__runicBootError = message;
  return { message };
};
