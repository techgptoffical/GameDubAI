"""Bound long English input without dropping words or silently truncating it."""
import re

def english_segments(text,encode,limit=96):
    normalized=' '.join(text.split())
    sentences=[]
    for piece in re.split(r'(?<=[.!?])\s+(?=[A-Z\"\'])',normalized):
        if sentences and re.search(r'\b(?:Mr|Mrs|Ms|Dr|St|Prof)\.$',sentences[-1]):sentences[-1]+=' '+piece
        elif piece:sentences.append(piece)
    output=[]
    for sentence in sentences:
        pending=[]
        for word in sentence.split():
            if len(encode(word))>limit:raise ValueError('A single source word exceeds the translation token limit')
            if pending and len(encode(' '.join(pending+[word])))>limit:
                breaks=[i+1 for i,w in enumerate(pending) if re.search(r'[,;:]$',w)]
                cut=next((i for i in reversed(breaks) if i>=len(pending)//2),len(pending))
                output.append(' '.join(pending[:cut]));pending=pending[cut:]
                if pending and len(encode(' '.join(pending+[word])))>limit:
                    output.append(' '.join(pending));pending=[]
            pending.append(word)
        if pending:output.append(' '.join(pending))
    if ' '.join(output)!=normalized:raise AssertionError('Source segmentation lost content')
    return output
