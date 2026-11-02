import json
import sys
sys.stdout.reconfigure(encoding='utf-8')

with open('blurbusters_inputlag.json', encoding='utf-8') as f:
    data = json.load(f)

print(f"Total threads: {len(data)}")
total_posts = sum(t['posts_count'] for t in data)
print(f"Total posts: {total_posts}")
print("\nTodos los titulos:")
for t in data:
    print(f"  [{t['thread_id']}] ({t['posts_count']} posts) {t['title']}")
