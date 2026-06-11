import pandas as pd
df = pd.read_csv('corpus_catalog.csv')
df['file_id'] = df['file_id'].str.lower().str.replace(' ', '_')
df.to_csv('corpus_catalog.csv', index=False)