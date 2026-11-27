// Self-test for intent-keeps-scope-as-typed (the LLM half): the scope arrives with the request, so the model must not ask
// for it, and must keep it as typed in the front matter and in the draft commit's header.
import { scopeTests } from './intent-scope-common.mjs';

scopeTests('intent-keeps-scope-as-typed', { asksNothing: true });
