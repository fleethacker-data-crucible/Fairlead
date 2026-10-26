curl <OPENAI_RESPONSES_URL> \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $DATABRICKS_TOKEN" \
  -d '{
    "model": "system.ai.grok-4-6",
    "max_output_tokens": 256,
    "input": [
      {
        "role": "user",
        "content": [{"type": "input_text", "text": "Hello!"}]
      },
      {
        "role": "assistant",
        "content": [{"type": "output_text", "text": "Hello! How can I assist you today?"}]
      },
      {
        "role": "user",
        "content": [{"type": "input_text", "text": "What is Databricks?"}]
      }
    ]
  }'

curl <MLFLOW_RESPONSES_URL> \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $DATABRICKS_TOKEN" \
  -d '{
    "model": "system.ai.gemini-3-8-flash",
    "max_output_tokens": 256,
    "input": [
      {
        "role": "user",
        "content": [{"type": "input_text", "text": "Hello!"}]
      },
      {
        "role": "assistant",
        "content": [{"type": "output_text", "text": "Hello! How can I assist you today?"}]
      },
      {
        "role": "user",
        "content": [{"type": "input_text", "text": "What is Databricks?"}]
      }
    ]
  }'

curl <MLFLOW_RESPONSES_URL> \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $DATABRICKS_TOKEN" \
  -d '{
    "model": "system.ai.gemini-3-7-flash",
    "max_output_tokens": 256,
    "input": [
      {
        "role": "user",
        "content": [{"type": "input_text", "text": "Hello!"}]
      },
      {
        "role": "assistant",
        "content": [{"type": "output_text", "text": "Hello! How can I assist you today?"}]
      },
      {
        "role": "user",
        "content": [{"type": "input_text", "text": "What is Databricks?"}]
      }
    ]
  }'

curl <MLFLOW_RESPONSES_URL> \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $DATABRICKS_TOKEN" \
  -d '{
    "model": "system.ai.gemini-3-5-flash-lite",
    "max_output_tokens": 256,
    "input": [
      {
        "role": "user",
        "content": [{"type": "input_text", "text": "Hello!"}]
      },
      {
        "role": "assistant",
        "content": [{"type": "output_text", "text": "Hello! How can I assist you today?"}]
      },
      {
        "role": "user",
        "content": [{"type": "input_text", "text": "What is Databricks?"}]
      }
    ]
  }'

curl <MLFLOW_RESPONSES_URL> \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $DATABRICKS_TOKEN" \
  -d '{
    "model": "system.ai.gemini-3-6-flash",
    "max_output_tokens": 256,
    "input": [
      {
        "role": "user",
        "content": [{"type": "input_text", "text": "Hello!"}]
      },
      {
        "role": "assistant",
        "content": [{"type": "output_text", "text": "Hello! How can I assist you today?"}]
      },
      {
        "role": "user",
        "content": [{"type": "input_text", "text": "What is Databricks?"}]
      }
    ]
  }'

curl <MLFLOW_RESPONSES_URL> \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $DATABRICKS_TOKEN" \
  -d '{
    "model": "system.ai.gpt-5-6-terra",
    "max_output_tokens": 256,
    "input": [
      {
        "role": "user",
        "content": [{"type": "input_text", "text": "Hello!"}]
      },
      {
        "role": "assistant",
        "content": [{"type": "output_text", "text": "Hello! How can I assist you today?"}]
      },
      {
        "role": "user",
        "content": [{"type": "input_text", "text": "What is Databricks?"}]
      }
    ]
  }'

curl <MLFLOW_RESPONSES_URL> \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $DATABRICKS_TOKEN" \
  -d '{
    "model": "system.ai.gpt-5-6-luna",
    "max_output_tokens": 256,
    "input": [
      {
        "role": "user",
        "content": [{"type": "input_text", "text": "Hello!"}]
      },
      {
        "role": "assistant",
        "content": [{"type": "output_text", "text": "Hello! How can I assist you today?"}]
      },
      {
        "role": "user",
        "content": [{"type": "input_text", "text": "What is Databricks?"}]
      }
    ]
  }'

curl <MLFLOW_RESPONSES_URL> \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $DATABRICKS_TOKEN" \
  -d '{
    "model": "system.ai.gemini-3-5-flash",
    "max_output_tokens": 256,
    "input": [
      {
        "role": "user",
        "content": [{"type": "input_text", "text": "Hello!"}]
      },
      {
        "role": "assistant",
        "content": [{"type": "output_text", "text": "Hello! How can I assist you today?"}]
      },
      {
        "role": "user",
        "content": [{"type": "input_text", "text": "What is Databricks?"}]
      }
    ]
  }'

curl <MLFLOW_RESPONSES_URL> \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $DATABRICKS_TOKEN" \
  -d '{
    "model": "system.ai.gpt-5-4",
    "max_output_tokens": 256,
    "input": [
      {
        "role": "user",
        "content": [{"type": "input_text", "text": "Hello!"}]
      },
      {
        "role": "assistant",
        "content": [{"type": "output_text", "text": "Hello! How can I assist you today?"}]
      },
      {
        "role": "user",
        "content": [{"type": "input_text", "text": "What is Databricks?"}]
      }
    ]
  }'
