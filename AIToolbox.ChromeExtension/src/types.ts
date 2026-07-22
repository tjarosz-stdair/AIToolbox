export type ExtensionSettings = {
  apiBaseUrl: string;
  clientId: string;
  authority: string;
  scope: string;
};

export type ParsedProduct = {
  id: number;
  productDataJson: string;
  productName?: string;
  productCode?: string;
  supplier?: string;
  manufacturer?: string;
  category?: string;
  primaryParsedProductId?: number;
  createdUtc?: string;
  updatedUtc?: string;
};

export type ParseBatchResult = {
  parseType: string;
  rejectedReason?: string;
  products: ParsedProduct[];
  productLibraryItemIds: number[];
};

export type ProductParserWorkspaceState = {
  view: "tools" | "parser" | "jobs";
  tab: "parse" | "library" | "detail";
  search: string;
  selectedProduct?: ParsedProduct;
  selectedSectionKey?: string;
  expandedSectionKeys: string[];
};

export type ActiveToolJob = {
  id: number;
  toolKey: string;
  title: string;
  status: string;
  createdUtc?: string;
  startedUtc?: string;
  elapsedSeconds?: number;
  canCancel?: boolean;
};

export type JobsSnapshot = {
  count: number;
  jobs: ActiveToolJob[];
  lastUpdatedUtc?: string;
  error?: string;
};
